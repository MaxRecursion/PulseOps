// PulseOps implementation for fixed telemetry fields, bounded parsing and SQLite.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PulseOps.Server;

internal sealed record EventSearch(string Sql, List<(string Name, object Value)> Parameters)
{
    public static bool TryCompile(string query, out EventSearch search)
    {
        search = new("1", []);
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (query.Length > 512)
            return false;
        var tokens = Regex.Matches(query, @"[()]|[^\s()]+").Select(match => match.Value).ToArray();
        if (tokens.Length is 0 or > 64)
            return false;
        try
        {
            var parser = new Parser(tokens);
            search = parser.Compile();
            return true;
        }
        catch (FormatException) { return false; }
    }

    private sealed class Parser(string[] tokens)
    {
        private readonly List<(string Name, object Value)> _parameters = [];
        private int _position;
        private string? Peek => _position < tokens.Length ? tokens[_position] : null;
        private bool Is(string token) => string.Equals(Peek, token, StringComparison.OrdinalIgnoreCase);

        public EventSearch Compile()
        {
            var filter = Or();
            if (Peek is not null)
                throw new FormatException();
            return new(filter, _parameters);
        }

        private string Or()
        {
            var result = And();
            while (Is("OR"))
            {
                _position++;
                result = $"({result} OR {And()})";
            }
            return result;
        }

        private string And()
        {
            var result = Unary();
            while (Peek is not null && !Is(")") && !Is("OR"))
            {
                if (Is("AND"))
                    _position++;
                result = $"({result} AND {Unary()})";
            }
            return result;
        }

        private string Unary()
        {
            if (Is("NOT"))
            {
                _position++;
                return $"NOT ({Unary()})";
            }
            if (Is("("))
            {
                _position++;
                var group = Or();
                if (!Is(")"))
                    throw new FormatException();
                _position++;
                return group;
            }
            string token = Peek ?? throw new FormatException();
            if (Is("AND") || Is("OR") || Is(")"))
                throw new FormatException();
            _position++;
            if (token.StartsWith('-') && token.Length > 1)
                return $"NOT ({Term(token[1..])})";
            return Term(token);
        }

        private string Term(string term)
        {
            const string atom = @"\A[A-Za-z0-9._*-]{1,64}\z";
            if (term == "*")
                return "1";
            int colon = term.IndexOf(':');
            if (colon < 0)
            {
                if (!Regex.IsMatch(term, atom))
                    throw new FormatException();
                return $"name GLOB {Bind(term.Contains('*') ? term : "*" + term + "*")}";
            }
            string key = term[..colon].ToLowerInvariant();
            string value = term[(colon + 1)..];
            string column = key switch
            {
                "kind" => "kind", "name" => "name", "outcome" => "outcome", "category" => "category",
                "status" => "status_code", "duration" => "duration_ms", "trace" => "trace_id",
                "session" => "session_id", "version" => "version", _ => throw new FormatException()
            };
            if (value == "*")
                return $"{column} IS NOT NULL";
            if (key is "status" or "duration")
            {
                var comparison = Regex.Match(value, @"\A(>=|<=|>|<|=)?([0-9]+(?:\.[0-9]+)?)\z");
                if (!comparison.Success || !double.TryParse(comparison.Groups[2].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                    throw new FormatException();
                string op = comparison.Groups[1].Success ? comparison.Groups[1].Value : "=";
                return $"COALESCE({column} {op} {Bind(number)},0)";
            }
            if (!Regex.IsMatch(value, atom))
                throw new FormatException();
            return $"COALESCE({column} {(value.Contains('*') ? "GLOB" : "=")} {Bind(value)},0)";
        }

        private string Bind(object value)
        {
            var name = $"$search{_parameters.Count}";
            _parameters.Add((name, value));
            return name;
        }
    }
}
