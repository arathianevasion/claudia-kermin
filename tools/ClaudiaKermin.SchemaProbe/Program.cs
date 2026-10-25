// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.SchemaProbe
{
    /// <summary>
    /// Does the API compile this structured-output schema (#199)? Sends one Messages request (max_tokens 16, a
    /// one-line user message, output_config.format = the schema, no retries) through the mod's ClaudeClient and
    /// prints the HTTP status and the API's error message. Never prints the key or any request header.
    /// The key is read from the file named by CLAUDIA_PROBE_KEY_FILE. With --count it only counts the schema's
    /// unions, optional parameters and size, and with --print it writes the schema; neither sends anything.
    /// Exit code 0: compiled (2xx) or counted; 1: the API refused or the request failed; 2: bad usage.
    /// </summary>
    public static class Program
    {
        private const string KeyFileVariable = "CLAUDIA_PROBE_KEY_FILE";

        /// <summary>The cheapest current model with structured outputs.</summary>
        private const string DefaultModel = "claude-haiku-5-5";

        private const string Usage =
            "usage: ClaudiaKermin.SchemaProbe <source> [--model <id>] [--count | --print]\n"
            + "  mission-reply-planning   the planning schema, as the mod sends it (Mission Plan, Settings, replies, changes)\n"
            + "  mission-reply-flight     the flight schema, as the mod sends it (Sequence Plan, Debrief, Alert, questions)\n"
            + "  mission-reply-all        every reply kind in one schema (what #199 replaced)\n"
            + "  mission-reply:<k>,<k>    the schema for these kinds: mission_plan, sequence_plan, debrief, answer, alert\n"
            + "  craft-check              CraftCheckRules.SchemaJson() as built\n"
            + "  schema.json              a schema file, e.g. tools/ClaudiaKermin.SchemaProbe/schemas/*.json\n"
            + "  --count counts only and --print writes the schema JSON; neither sends anything.\n"
            + "  The API key is read from the file named by " + KeyFileVariable + ".";

        private const string KindsPrefix = "mission-reply:";

        public static int Main(string[] args)
        {
            string source = null;
            var model = DefaultModel;
            var countOnly = false;
            var print = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--count") countOnly = true;
                else if (args[i] == "--print") print = true;
                else if (args[i] == "--model" && i + 1 < args.Length) model = args[++i];
                else if (source == null && !args[i].StartsWith("--", StringComparison.Ordinal)) source = args[i];
                else return Fail(Usage);
            }
            if (source == null) return Fail(Usage);

            string schema;
            if (source == "mission-reply-planning") schema = MissionReply.PlanningSchemaJson;
            else if (source == "mission-reply-flight") schema = MissionReply.FlightSchemaJson;
            else if (source == "mission-reply-all") schema = MissionReply.SchemaFor(MissionReply.AllKinds);
            else if (source.StartsWith(KindsPrefix, StringComparison.Ordinal))
            {
                var kinds = source.Substring(KindsPrefix.Length).Split(',');
                var unknown = kinds.FirstOrDefault(k => !MissionReply.AllKinds.Contains(k));
                if (unknown != null) return Fail("unknown kind: " + unknown + "\n" + Usage);
                schema = MissionReply.SchemaFor(kinds);
            }
            else if (source == "craft-check") schema = CraftCheckRules.SchemaJson();
            else if (File.Exists(source)) schema = File.ReadAllText(source).Trim();
            else return Fail("not found: " + source + "\n" + Usage);

            if (print)
            {
                Console.WriteLine(schema);
                return 0;
            }

            int unions, optional, size;
            try
            {
                unions = SchemaComplexity.Unions(schema);
                optional = SchemaComplexity.OptionalParameters(schema);
                size = SchemaComplexity.Size(schema);
            }
            catch (JsonException e)
            {
                return Fail("not a JSON schema: " + e.Message);
            }
            Console.WriteLine("schema: " + source + " unions=" + Number(unions) + " optional=" + Number(optional) + " size=" + Number(size));
            if (countOnly) return 0;

            var keyFile = Environment.GetEnvironmentVariable(KeyFileVariable);
            if (string.IsNullOrEmpty(keyFile) || !File.Exists(keyFile)) return Fail("set " + KeyFileVariable + " to a file holding the API key");
            var key = File.ReadAllText(keyFile).Trim();
            if (key.Length == 0) return Fail("the key file is empty");

            // The client's log lines hold no key or headers, but the probe prints nothing it doesn't need.
            var client = new ClaudeClient(new HttpWebRequestTransport(), () => key, line => { });
            client.Timing.RetriesBeforeContent = 0;
            client.Timing.RetriesAfterContent = 0;
            var request = new MessagesRequest { Model = model, MaxTokens = 16, OutputSchemaJson = schema };
            request.Messages.Add(Message.UserText("Reply with kind answer and say ok."));

            var result = client.Stream(request, null, CancellationToken.None);
            Console.WriteLine("model: " + model);
            if (result.HttpStatus == 0)
            {
                Console.WriteLine("no HTTP response (" + result.Failure + ")");
                return 1;
            }
            Console.WriteLine("HTTP " + Number(result.HttpStatus));
            if (result.ErrorMessage != null) Console.WriteLine("error: " + result.ErrorMessage);
            if (result.Status == StreamStatus.Completed)
            {
                Console.WriteLine("compiled (stop_reason " + result.Message.RawStopReason + ")");
                return 0;
            }
            // A 2xx means the schema compiled, even if the stream broke off afterwards.
            Console.WriteLine("failed (" + result.Failure + ")");
            return result.HttpStatus >= 200 && result.HttpStatus < 300 ? 0 : 1;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
