using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jasper.Persistence.Durability;
using Jasper.Transports;
using Jasper.Util;
using Weasel.Core;

namespace Jasper.Persistence.Database
{
    public abstract class DatabaseBackedEnvelopePersistence : IEnvelopePersistence, IDurableOutgoing
    {
        protected readonly CancellationToken _cancellation;
        private readonly string _incrementIncomingAttempts;

        private readonly string _storeIncoming;
        protected readonly DurableStorageSession _session;
        private readonly string _findReadyToExecuteJobs;
        private readonly string _fetchOwnersSql;
        private readonly string _reassignDormantNodeSql;
        private readonly string _findUniqueDestinations;
        private readonly string _deleteOutgoingSql;

        private readonly string _findOutgoingEnvelopesSql;
        private readonly string _insertOutgoingSql;

        protected DatabaseBackedEnvelopePersistence(DatabaseSettings databaseSettings, AdvancedSettings settings,
            IEnvelopeStorageAdmin admin)
        {
            DatabaseSettings = databaseSettings;
            Admin = admin;

            Settings = settings;
            _cancellation = settings.Cancellation;

            _incrementIncomingAttempts =
                $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set attempts = @attempts where id = @id";
            _storeIncoming = $@"
insert into {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}
  (id, status, owner_id, execution_time, attempts, body)
values
  (@id, @status, @owner, @time, @attempts, @body);
";

            var transaction = new DurableStorageSession(databaseSettings, settings.Cancellation);

            _session = transaction;
            Session = transaction;

            // ReSharper disable once VirtualMemberCallInConstructor
            Incoming = buildDurableIncoming(transaction, databaseSettings, settings);

            _findReadyToExecuteJobs =
                $"select body, attempts from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @time";

            _cancellation = settings.Cancellation;

            _fetchOwnersSql = $@"
select distinct owner_id from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id != 0 and owner_id != @owner
union
select distinct owner_id from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id != 0 and owner_id != @owner";

            _reassignDormantNodeSql = $@"
update {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}
  set owner_id = 0
where
  owner_id = @owner;

update {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}
  set owner_id = 0
where
  owner_id = @owner;
";

            _findUniqueDestinations =
                $"select distinct destination from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}";

            _deleteOutgoingSql =
                $"delete from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = :owner and destination = @destination";

            _cancellation = settings.Cancellation;

            _findOutgoingEnvelopesSql =
                determineOutgoingEnvelopeSql(databaseSettings, settings);

            _insertOutgoingSql =
                $"insert into {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} (id, owner_id, destination, deliver_by, body) values (@id, @owner, @destination, @deliverBy, @body)";


        }

        public AdvancedSettings Settings { get; }

        public DatabaseSettings DatabaseSettings { get; }

        public IEnvelopeStorageAdmin Admin { get; }

        public IDurableStorageSession Session { get; }
        public IDurableIncoming Incoming { get; }
        public IDurableOutgoing Outgoing => this;




        public Task DeleteIncomingEnvelope(Envelope envelope)
        {
            return DatabaseSettings
                .CreateCommand($"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where id = @id")
                .With("id", envelope.Id)
                .ExecuteOnce(_cancellation);
        }


        public Task ScheduleExecution(Envelope[] envelopes)
        {
            var builder = DatabaseSettings.ToCommandBuilder();

            foreach (var envelope in envelopes)
            {
                var id = builder.AddParameter(envelope.Id);
                var time = builder.AddParameter(envelope.ExecutionTime.Value);
                var attempts = builder.AddParameter(envelope.Attempts);

                builder.Append(
                    $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @{time.ParameterName}, status = \'{EnvelopeStatus.Scheduled}\', attempts = @{attempts.ParameterName}, owner_id = {TransportConstants.AnyNode} where id = @{id.ParameterName};");
            }

            return builder.Compile().ExecuteOnce(_cancellation);
        }


        public Task MoveToDeadLetterStorage(Envelope envelope, Exception ex)
        {
            return MoveToDeadLetterStorage(new[] {new ErrorReport(envelope, ex)});
        }

        public Task IncrementIncomingEnvelopeAttempts(Envelope envelope)
        {
            return DatabaseSettings.CreateCommand(_incrementIncomingAttempts)
                .With("attempts", envelope.Attempts)
                .With("id", envelope.Id)
                .ExecuteOnce(_cancellation);
        }

        public Task StoreIncoming(Envelope envelope)
        {
            return DatabaseSettings.CreateCommand(_storeIncoming)
                .With("id", envelope.Id)
                .With("status", envelope.Status.ToString())
                .With("owner", envelope.OwnerId)
                .With("attempts", envelope.Attempts)
                .With("time", envelope.ExecutionTime)
                .With("body", envelope.Serialize())
                .ExecuteOnce(_cancellation);
        }

        public async Task StoreIncoming(Envelope[] envelopes)
        {
            var cmd = BuildIncomingStorageCommand(envelopes, DatabaseSettings);

            using (var conn = DatabaseSettings.CreateConnection())
            {
                await conn.OpenAsync(_cancellation);

                cmd.Connection = conn;

                await cmd.ExecuteNonQueryAsync(_cancellation);
            }
        }


        public Task ScheduleJob(Envelope envelope)
        {
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.OwnerId = TransportConstants.AnyNode;

            return StoreIncoming(envelope);
        }


        public abstract Task MoveToDeadLetterStorage(ErrorReport[] errors);
        public abstract Task DeleteIncomingEnvelopes(Envelope[] envelopes);

        public abstract void Describe(TextWriter writer);

        public Task StoreIncoming(DbTransaction tx, Envelope[] envelopes)
        {
            var cmd = BuildIncomingStorageCommand(envelopes, DatabaseSettings);

            cmd.Transaction = tx;
            cmd.Connection = tx.Connection;

            return cmd.ExecuteNonQueryAsync(_cancellation);
        }



        internal static DbCommand BuildIncomingStorageCommand(IEnumerable<Envelope> envelopes,
            DatabaseSettings settings)
        {
            var builder = settings.ToCommandBuilder();

            foreach (var envelope in envelopes)
            {
                var id = builder.AddParameter(envelope.Id);
                var status = builder.AddParameter(envelope.Status.ToString());
                var owner = builder.AddParameter(envelope.OwnerId);
                var attempts = builder.AddParameter(envelope.Attempts);
                var time = builder.AddParameter(envelope.ExecutionTime);
                var body = builder.AddParameter(envelope.Serialize());


                builder.Append(
                    $"insert into {settings.SchemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, execution_time, attempts, body) values (@{id.ParameterName}, @{status.ParameterName}, @{owner.ParameterName}, @{time.ParameterName}, @{attempts.ParameterName}, @{body.ParameterName});");
            }


            return builder.Compile();
        }



        public void ClearAllStoredMessages()
        {
            DatabaseSettings
                .ExecuteSql(
                    $"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable};delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable};delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable}");
        }

        public Envelope[] AllIncomingEnvelopes()
        {
            using (var conn = DatabaseSettings.CreateConnection())
            {
                conn.Open();

                return conn
                    .CreateCommand(
                        $"select body, status, owner_id, execution_time, attempts from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}")
                    .LoadEnvelopes();
            }
        }

        public Envelope[] AllOutgoingEnvelopes()
        {
            using (var conn = DatabaseSettings.CreateConnection())
            {
                conn.Open();

                return conn
                    .CreateCommand(
                        $"select body, '{EnvelopeStatus.Outgoing}', owner_id, NULL from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}")
                    .LoadEnvelopes();
            }
        }

        public Task<Envelope[]> LoadScheduledToExecute(DateTimeOffset utcNow)
        {
            return _session
                .CreateCommand(_findReadyToExecuteJobs)
                .With("time", utcNow)
                .ExecuteToEnvelopesWithAttempts(_cancellation, _session.Transaction);
        }

        public Task ReassignDormantNodeToAnyNode(int nodeId)
        {
            return _session.CreateCommand(_reassignDormantNodeSql)
                .With("owner", nodeId)
                .ExecuteNonQueryAsync(_cancellation);
        }

        public async Task<int[]> FindUniqueOwners(int currentNodeId)
        {
            var list = new List<int>();
            using (var reader = await _session.CreateCommand(_fetchOwnersSql)
                .With("owner", currentNodeId)
                .ExecuteReaderAsync(_cancellation))
            {
                while (await reader.ReadAsync(_cancellation))
                {
                    var id = await reader.GetFieldValueAsync<int>(0, _cancellation);
                    list.Add(id);
                }
            }

            return list.ToArray();
        }

        protected abstract IDurableIncoming buildDurableIncoming(DurableStorageSession durableStorageSession,
            DatabaseSettings databaseSettings, AdvancedSettings settings);

        protected abstract string determineOutgoingEnvelopeSql(DatabaseSettings databaseSettings, AdvancedSettings settings);

        public Task<Envelope[]> Load(Uri destination)
        {
            return _session.CreateCommand(_findOutgoingEnvelopesSql)
                .With("destination", destination.ToString())
                .ExecuteToEnvelopes(_cancellation);
        }

        public abstract Task ReassignOutgoing(int ownerId, Envelope[] outgoing);

        public Task DeleteByDestination(Uri destination)
        {
            return _session.CreateCommand(_deleteOutgoingSql)
                .With("destination", destination.ToString())
                .With("owner", TransportConstants.AnyNode)
                .ExecuteNonQueryAsync(_cancellation);
        }


        public Task DeleteOutgoing(Envelope envelope)
        {
            return DatabaseSettings
                .CreateCommand($"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
                .With("id", envelope.Id)
                .ExecuteOnce(_cancellation);
        }

        public async Task<Uri[]> FindAllDestinations()
        {
            var list = new List<Uri>();

            var cmd = _session.CreateCommand(_findUniqueDestinations);
            using (var reader = await cmd.ExecuteReaderAsync(_cancellation))
            {
                while (await reader.ReadAsync(_cancellation))
                {
                    var text = await reader.GetFieldValueAsync<string>(0, _cancellation);
                    list.Add(text.ToUri());
                }
            }

            return list.ToArray();
        }

        public abstract Task DiscardAndReassignOutgoing(Envelope[] discards, Envelope[] reassigned, int nodeId);
        public abstract Task DeleteOutgoing(Envelope[] envelopes);

        public Task StoreOutgoing(Envelope envelope, int ownerId)
        {
            return DatabaseSettings.CreateCommand(_insertOutgoingSql)
                .With("id", envelope.Id)
                .With("owner", ownerId)
                .With("destination", envelope.Destination.ToString())
                .With("deliverBy", envelope.DeliverBy)
                .With("body", envelope.Serialize())
                .ExecuteOnce(_cancellation);
        }

        public Task StoreOutgoing(Envelope[] envelopes, int ownerId)
        {
            var cmd = BuildOutgoingStorageCommand(envelopes, ownerId, DatabaseSettings);
            return cmd.ExecuteOnce(CancellationToken.None);
        }

        public Task StoreOutgoing(DbTransaction tx, Envelope[] envelopes)
        {
            var cmd = BuildOutgoingStorageCommand(envelopes, Settings.UniqueNodeId, DatabaseSettings);
            cmd.Connection = tx.Connection;
            cmd.Transaction = tx;

            return cmd.ExecuteNonQueryAsync(_cancellation);
        }

            public static DbCommand BuildOutgoingStorageCommand(Envelope[] envelopes, int ownerId,
                DatabaseSettings settings)
            {
                var builder = settings.ToCommandBuilder();

                builder.AddNamedParameter("owner", ownerId).DbType = DbType.Int32;

                foreach (var envelope in envelopes)
                {
                    var id = builder.AddParameter(envelope.Id);
                    var destination = builder.AddParameter(envelope.Destination.ToString());
                    var deliverBy = builder.AddParameter(envelope.DeliverBy);
                    var body = builder.AddParameter(envelope.Serialize());

                    builder.Append(
                        $"insert into {settings.SchemaName}.{DatabaseConstants.OutgoingTable} (id, owner_id, destination, deliver_by, body) values (@{id.ParameterName}, @owner, @{destination.ParameterName}, @{deliverBy.ParameterName}, @{body.ParameterName});");
                }

                return builder.Compile();
            }


        public void Dispose()
        {
            Session?.Dispose();
        }
    }
}
