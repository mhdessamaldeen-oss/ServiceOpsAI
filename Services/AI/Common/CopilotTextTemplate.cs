using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Scriban;
using Scriban.Runtime;

namespace ServiceOpsAI.Services.AI.Common
{
    public static class CopilotTextTemplate
    {
        private static readonly Regex PlaceholderPattern = new(@"%([A-Z0-9_]+)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly ConcurrentDictionary<string, Template> TemplateCache = new(StringComparer.Ordinal);

        public static string JoinLines(IEnumerable<string> lines)
            => string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));

        public static string Apply(string template, IReadOnlyDictionary<string, string?> values)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            var scribanTemplate = TemplateCache.GetOrAdd(template, static source =>
            {
                var converted = PlaceholderPattern.Replace(source, "{{ $1 }}");
                var parsed = Template.Parse(converted);
                if (parsed.HasErrors)
                {
                    throw new InvalidOperationException($"Invalid copilot text template: {string.Join("; ", parsed.Messages.Select(message => message.Message))}");
                }

                return parsed;
            });

            var scriptObject = new ScriptObject();
            foreach (var pair in values)
            {
                scriptObject.Add(pair.Key, pair.Value ?? string.Empty);
            }

            var context = new TemplateContext();
            context.PushGlobal(scriptObject);
            return scribanTemplate.Render(context);
        }
    }
}
