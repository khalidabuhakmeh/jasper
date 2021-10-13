﻿using System;
using System.Data;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Baseline;
using Jasper.Logging;
using Jasper.Persistence.Database;
using Jasper.Persistence.Durability;
using Jasper.Persistence.Postgresql.Util;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using CommandExtensions = Jasper.Persistence.Postgresql.Util.CommandExtensions;

namespace Jasper.Persistence.Postgresql.Schema
{
    internal class OutgoingEnvelopeTable : Table
    {
        public static readonly string TableName = "jasper_outgoing_envelopes";

        public OutgoingEnvelopeTable(string schemaName) : base(new DbObjectName(schemaName, TableName))
        {
            AddColumn<Guid>("id").AsPrimaryKey();
            AddColumn<int>("owner_id").NotNull();
            AddColumn<string>("destination").NotNull();
            AddColumn<DateTimeOffset>("deliver_by");
            AddColumn("body", "bytea").NotNull();
        }
    }

    internal class IncomingEnvelopeTable : Table
    {
        public static readonly string TableName = "jasper_incoming_envelopes";

        public IncomingEnvelopeTable(string schemaName) : base(new DbObjectName(schemaName, TableName))
        {
            AddColumn<Guid>("id").AsPrimaryKey();
            AddColumn<string>("status").NotNull();
            AddColumn<int>("owner_id").NotNull();
            AddColumn<DateTimeOffset>("execution_time").DefaultValueByExpression("NULL");
            AddColumn<int>("attempts");
            AddColumn("body", "bytea").NotNull();
        }
    }

    internal class DeadLettersTable : Table
    {
        public static readonly string TableName = "jasper_dead_letters";

        public DeadLettersTable(string schemaName) : base(new DbObjectName(schemaName, TableName))
        {
            AddColumn<Guid>("id").AsPrimaryKey();
            AddColumn<string>("source");
            AddColumn<string>("message_type");
            AddColumn<string>("explanation");
            AddColumn<string>("exception_text");
            AddColumn<string>("exception_type");
            AddColumn<string>("exception_message");
            AddColumn("body", "bytea").NotNull();
        }
    }

    public class PostgresqlEnvelopeStorageAdmin : DataAccessor, IEnvelopeStorageAdmin
    {
        private readonly string _connectionString;
        private readonly Table[] _tables;

        public PostgresqlEnvelopeStorageAdmin(PostgresqlSettings settings)
        {
            _connectionString = settings.ConnectionString;
            SchemaName = settings.SchemaName;

            _tables = new Table[]
            {
                new OutgoingEnvelopeTable(SchemaName),
                new IncomingEnvelopeTable(SchemaName),
                new DeadLettersTable(SchemaName)
            };
        }

        public string SchemaName { get; set; } = "public";

        public async Task DropAll()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            foreach (var table in _tables)
            {
                await table.Drop(conn);
            }
        }

        public async Task CreateAll()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var patch = await SchemaMigration.Determine(conn, _tables);

            await patch.ApplyAll(conn, new DdlRules(), AutoCreate.CreateOrUpdate);
        }

        public async Task RecreateAll()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            foreach (var table in _tables)
            {
                await table.Drop(conn);
            }

            var patch = await SchemaMigration.Determine(conn, _tables);

            await patch.ApplyAll(conn, new DdlRules(), AutoCreate.CreateOrUpdate);
        }

        async Task IEnvelopeStorageAdmin.ClearAllPersistedEnvelopes()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await truncateEnvelopeData(conn);
        }

        private async Task truncateEnvelopeData(NpgsqlConnection conn)
        {
            try
            {
                await conn.CreateCommand(
                        $"truncate table {SchemaName}.{OutgoingTable};truncate table {SchemaName}.{IncomingTable};truncate table {SchemaName}.{DeadLetterTable};")
                    .ExecuteNonQueryAsync();
            }
            catch (Exception e)
            {
                await Task.Delay(250);
                try
                {
                    await truncateEnvelopeData(conn);
                    return;
                }
                catch (Exception)
                {
                    // Just let the handler throw in a second
                }

                throw new InvalidOperationException(
                    "Failure trying to execute the truncate table statements for the envelope storage", e);
            }
        }

        async Task IEnvelopeStorageAdmin.RebuildSchemaObjects()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var patch = await SchemaMigration.Determine(conn, _tables);

            await patch.ApplyAll(conn, new DdlRules(), AutoCreate.CreateOrUpdate);

            await truncateEnvelopeData(conn);
        }

        string IEnvelopeStorageAdmin.CreateSql()
        {
            var writer = new StringWriter();
            writer.WriteLine($"CREATE SCHEMA IF NOT EXISTS {SchemaName};");
            writer.WriteLine();

            var rules = new DdlRules();
            foreach (var table in _tables)
            {
                table.WriteCreateStatement(rules, writer);
            }


            return writer.ToString();
        }

        public async Task<PersistedCounts> GetPersistedCounts()
        {
            var counts = new PersistedCounts();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();


                using (var reader = await conn
                    .CreateCommand(
                        $"select status, count(*) from {SchemaName}.{IncomingTable} group by status")
                    .ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                        var count = await reader.GetFieldValueAsync<int>(1);

                        if (status == EnvelopeStatus.Incoming)
                            counts.Incoming = count;
                        else if (status == EnvelopeStatus.Scheduled) counts.Scheduled = count;
                    }
                }

                var longCount = await conn
                    .CreateCommand($"select count(*) from {SchemaName}.{OutgoingTable}").ExecuteScalarAsync();

                counts.Outgoing =  Convert.ToInt32(longCount);
            }


            return counts;
        }

        public async Task<ErrorReport> LoadDeadLetterEnvelope(Guid id)
        {
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                var cmd = conn.CreateCommand(
                    $"select body, explanation, exception_text, exception_type, exception_message, source, message_type, id from {SchemaName}.{DeadLetterTable} where id = @id");
                cmd.With("id", id);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync()) return null;


                    var report = new ErrorReport
                    {
                        RawData = await reader.GetFieldValueAsync<byte[]>(0),
                        Explanation = await reader.GetFieldValueAsync<string>(1),
                        ExceptionText = await reader.GetFieldValueAsync<string>(2),
                        ExceptionType = await reader.GetFieldValueAsync<string>(3),
                        ExceptionMessage = await reader.GetFieldValueAsync<string>(4),
                        Source = await reader.GetFieldValueAsync<string>(5),
                        MessageType = await reader.GetFieldValueAsync<string>(6),
                        Id = await reader.GetFieldValueAsync<Guid>(7)
                    };

                    return report;
                }
            }
        }

        public async Task<Envelope[]> AllIncomingEnvelopes()
        {
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                return await conn.CreateCommand($"select body, status, owner_id from {SchemaName}.{IncomingTable}").ExecuteToEnvelopes();
            }
        }

        public async Task<Envelope[]> AllOutgoingEnvelopes()
        {
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                return await conn.CreateCommand($"select body, '{EnvelopeStatus.Outgoing}', owner_id from {SchemaName}.{OutgoingTable}").ExecuteToEnvelopes();
            }
        }

        public async Task ReleaseAllOwnership()
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await conn.CreateCommand($"update {SchemaName}.{IncomingTable} set owner_id = 0;update {SchemaName}.{OutgoingTable} set owner_id = 0")
                .ExecuteNonQueryAsync();
        }
    }
}
