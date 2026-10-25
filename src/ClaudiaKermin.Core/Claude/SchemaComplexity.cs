// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// Counts what makes a strict schema's compiled grammar grow, against the structured outputs "Schema complexity
    /// limits" (https://platform.claude.com/docs/en/build-with-claude/structured-outputs#schema-complexity-limits):
    /// at most 16 parameters with union types and 24 optional parameters per request, and an internal grammar-size
    /// limit below those once nesting counts (#199).
    /// </summary>
    internal static class SchemaComplexity
    {
        /// <summary>Every <c>anyOf</c> and every type array (<c>"type": ["number", "null"]</c>) in the schema.</summary>
        public static int Unions(string schemaJson)
        {
            return Unions(JsonReader.Parse(schemaJson));
        }

        /// <summary>Every property an object schema lists but does not require.</summary>
        public static int OptionalParameters(string schemaJson)
        {
            return Optional(JsonReader.Parse(schemaJson));
        }

        private static int Unions(JsonNode node)
        {
            var count = 0;
            if (node.Kind == JsonKind.Object)
            {
                var anyOf = node["anyOf"];
                if (anyOf != null && anyOf.Kind == JsonKind.Array) count++;
                var type = node["type"];
                if (type != null && type.Kind == JsonKind.Array) count++;
                count += node.Members.Sum(m => Unions(m.Value));
            }
            else if (node.Kind == JsonKind.Array)
            {
                count += node.Items.Sum(Unions);
            }
            return count;
        }

        private static int Optional(JsonNode node)
        {
            var count = 0;
            if (node.Kind == JsonKind.Object)
            {
                var properties = node["properties"];
                if (properties != null && properties.Kind == JsonKind.Object)
                {
                    var required = node["required"];
                    var names = required == null ? new string[0] : required.Items.Select(JsonNode.Str).ToArray();
                    count += properties.Members.Count(p => !names.Contains(p.Key));
                }
                count += node.Members.Sum(m => Optional(m.Value));
            }
            else if (node.Kind == JsonKind.Array)
            {
                count += node.Items.Sum(Optional);
            }
            return count;
        }
    }
}
