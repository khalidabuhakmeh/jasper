﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Jasper;
using Jasper.Attributes;
using Jasper.Configuration;
using Jasper.Runtime.Handlers;
using Jasper.Util;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;
using Oakton;

[assembly: JasperModule]

namespace Jasper.JsonCommands
{
    [Description("Exports Json schema documents for all handled message types in the current application", Name =
        "export-json-schema")]
    public class ExportJsonSchemaCommand : OaktonCommand<ExportJsonSchemaInput>
    {
        public override bool Execute(ExportJsonSchemaInput input)
        {
            var system = new FileSystem();
            var directory = input.Directory.ToFullPath();

            if (Directory.Exists(directory))
            {
                Console.WriteLine("Deleting contents of " + directory);
                system.CleanDirectory(directory);
            }
            else
            {
                Console.WriteLine("Creating directory " + directory);
                system.CreateDirectory(directory);
            }

            using (var host = input.BuildHost())
            {
                var handlers = host.Services.GetRequiredService<HandlerGraph>();
                var messageTypes = handlers.Chains.Select(x => x.MessageType)
                    .Where(x => x.Assembly != typeof(JasperOptions).Assembly);
                foreach (var messageType in messageTypes)
                {
                    var filename = $"{messageType.ToMessageTypeName()}.json";

                    var schema = JsonSchema.FromType(messageType);
                    var path = directory.AppendPath(filename);

                    Console.WriteLine("Writing file " + path);
                    system.WriteStringToFile(path, schema.ToJson());
                }
            }

            return true;
        }
    }

    public class ExportJsonSchemaInput : NetCoreInput
    {
        [Description("Target directory to write the json schema documents")]
        public string Directory { get; set; }
    }
}
