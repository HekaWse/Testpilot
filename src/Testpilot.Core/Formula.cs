using System.Globalization;
using System.Text;

namespace Testpilot.Core;

/// <summary>Error in a formula or in one of its references.</summary>
public sealed class FormulaException(string message) : Exception(message);

/// <summary>
/// Small, deterministic formula language for Calculate steps and DUT commands. Grammar:
/// <code>
/// formula    := comparison
/// comparison := additive [ ("==" | "!=" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=") additive ]   (result 1 or 0)
/// additive   := term { ("+" | "-") term }
/// term       := unary { ("*" | "/") unary }
/// unary      := "-" unary | primary
/// primary    := number | "{" reference "}" | function "(" formula { "," formula } ")" | "(" formula ")"
/// </code>
/// A reference names a value of the run, e.g. <c>{V103}</c> (value of the step "V103"), <c>{GS lesen:rssi868}</c>
/// (output of a DUT command) or <c>{data[1]}</c> (element 1 of a list). Functions: abs, trunc, round, min, max,
/// mean, sum, count. min/max/mean/sum/count also accept a list, e.g. <c>mean({Empfang:rssi})</c>.
/// </summary>
public static class Formula
{
    private static readonly string[] Functions = ["abs", "trunc", "round", "min", "max", "mean", "sum", "count"];

    /// <summary>Evaluates the formula to one number. <paramref name="resolve"/> returns the values of a reference.</summary>
    public static double Evaluate(string formula, Func<string, IReadOnlyList<double>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        var values = new Parser(formula, resolve).ParseAll();
        return Scalar(values, formula);
    }

    /// <summary>Checks the syntax and returns the references (without index), e.g. "V103" or "GS lesen:rssi868".</summary>
    public static IReadOnlyList<string> Validate(string formula)
    {
        var references = new List<string>();
        _ = new Parser(formula, reference =>
        {
            references.Add(reference);
            return [1d];
        }, validateOnly: true).ParseAll();
        return references.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Replaces each <c>{reference}</c> in a text with its value, e.g. "{major}.{minor}.{deb}" -> "1.6.20".
    /// Numbers are written invariantly; lists are joined with ",".
    /// </summary>
    public static string FormatText(string template, Func<string, IReadOnlyList<double>> resolve)
    {
        ArgumentNullException.ThrowIfNull(template);
        var builder = new StringBuilder();
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf('{', position);
            if (open < 0)
            {
                builder.Append(template, position, template.Length - position);
                break;
            }
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
                throw new FormulaException($"Missing '}}' in text '{template}'.");
            builder.Append(template, position, open - position);
            var values = ResolveReference(template[(open + 1)..close], resolve);
            builder.Append(string.Join(",", values.Select(value => value.ToString("G15", CultureInfo.InvariantCulture))));
            position = close + 1;
        }
        return builder.ToString();
    }

    /// <summary>Resolves a reference, including an optional index such as <c>data[1]</c>.</summary>
    public static IReadOnlyList<double> ResolveReference(string reference, Func<string, IReadOnlyList<double>> resolve)
    {
        var (name, index) = SplitReference(reference);
        var values = resolve(name);
        if (index is null)
            return values;
        if (index.Value >= values.Count)
            throw new FormulaException($"Reference '{{{reference.Trim()}}}': index {index} does not exist ({values.Count} values).");
        return [values[index.Value]];
    }

    private static (string Name, int? Index) SplitReference(string reference)
    {
        var text = reference.Trim();
        if (text.Length == 0)
            throw new FormulaException("Empty reference '{}'.");
        if (!text.EndsWith(']'))
            return (text, null);
        var open = text.LastIndexOf('[');
        if (open <= 0 || !int.TryParse(text[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            throw new FormulaException($"Invalid index in reference '{{{text}}}'.");
        return (text[..open].TrimEnd(), index);
    }

    private static double Scalar(IReadOnlyList<double> values, string context) => values.Count == 1
        ? values[0]
        : throw new FormulaException(
            $"'{context}' has {values.Count} values instead of one; use mean(), min(), max() or an index like [0].");

    private sealed class Parser(string text, Func<string, IReadOnlyList<double>> resolve, bool validateOnly = false)
    {
        private int position;

        public IReadOnlyList<double> ParseAll()
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormulaException("The formula is empty.");
            var result = ParseComparison();
            SkipWhitespace();
            if (position < text.Length)
                throw Error($"Unexpected '{text[position]}'");
            return result;
        }

        private IReadOnlyList<double> ParseComparison()
        {
            var left = ParseAdditive();
            SkipWhitespace();
            var op = new[] { "==", "!=", "<=", ">=", "<", ">" }
                .FirstOrDefault(candidate => string.CompareOrdinal(text, position, candidate, 0, candidate.Length) == 0);
            if (op is null)
                return left;
            position += op.Length;
            var a = Scalar(left, text);
            var b = Scalar(ParseAdditive(), text);
            var result = op switch
            {
                "==" => a == b,
                "!=" => a != b,
                "<=" => a <= b,
                ">=" => a >= b,
                "<" => a < b,
                _ => a > b
            };
            return [result ? 1d : 0d];
        }

        private IReadOnlyList<double> ParseAdditive()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (!Accept('+') && !Accept('-'))
                    return left;
                var op = text[position - 1];
                var right = ParseTerm();
                left = [op == '+' ? Scalar(left, text) + Scalar(right, text) : Scalar(left, text) - Scalar(right, text)];
            }
        }

        private IReadOnlyList<double> ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (!Accept('*') && !Accept('/'))
                    return left;
                var op = text[position - 1];
                var right = ParseUnary();
                left = [op == '*' ? Scalar(left, text) * Scalar(right, text) : Scalar(left, text) / Scalar(right, text)];
            }
        }

        private IReadOnlyList<double> ParseUnary()
        {
            SkipWhitespace();
            if (Accept('-'))
                return [-Scalar(ParseUnary(), text)];
            if (Accept('+'))
                return ParseUnary();
            return ParsePrimary();
        }

        private IReadOnlyList<double> ParsePrimary()
        {
            SkipWhitespace();
            if (position >= text.Length)
                throw Error("Unexpected end of the formula");
            var current = text[position];
            if (current == '(')
            {
                position++;
                var inner = ParseComparison();
                Expect(')');
                return inner;
            }
            if (current == '{')
            {
                var close = text.IndexOf('}', position + 1);
                if (close < 0)
                    throw Error("Missing '}'");
                var reference = text[(position + 1)..close];
                position = close + 1;
                if (validateOnly)
                    return resolve(SplitReference(reference).Name);
                var values = ResolveReference(reference, resolve);
                if (values.Count == 0)
                    throw new FormulaException($"Reference '{{{reference.Trim()}}}' has no values.");
                return values;
            }
            if (char.IsDigit(current) || current == '.')
                return [ParseNumber()];
            if (char.IsLetter(current))
                return ParseFunction();
            throw Error($"Unexpected '{current}'");
        }

        private double ParseNumber()
        {
            var start = position;
            while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
                position++;
            if (position < text.Length && text[position] is 'e' or 'E')
            {
                position++;
                if (position < text.Length && text[position] is '+' or '-')
                    position++;
                while (position < text.Length && char.IsDigit(text[position]))
                    position++;
            }
            var number = text[start..position];
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw Error($"Invalid number '{number}'");
        }

        private IReadOnlyList<double> ParseFunction()
        {
            var start = position;
            while (position < text.Length && char.IsLetter(text[position]))
                position++;
            var name = text[start..position];
            if (!Functions.Contains(name, StringComparer.Ordinal))
                throw Error($"Unknown function '{name}' (allowed: {string.Join(", ", Functions)})");
            Expect('(');
            var arguments = new List<IReadOnlyList<double>> { ParseComparison() };
            SkipWhitespace();
            while (Accept(','))
                arguments.Add(ParseComparison());
            Expect(')');
            // A single list argument is used as the list of values (mean({rssi})); otherwise each argument is one value.
            var values = arguments.Count == 1 ? arguments[0] : arguments.Select(argument => Scalar(argument, text)).ToArray();
            if (values.Count == 0)
                throw Error($"{name}() has no values");
            return name switch
            {
                "abs" => [Math.Abs(Single(values, name))],
                "trunc" => [Math.Truncate(Single(values, name))],
                "round" => [Math.Round(Single(values, name), MidpointRounding.AwayFromZero)],
                "min" => [values.Min()],
                "max" => [values.Max()],
                "mean" => [values.Average()],
                "sum" => [values.Sum()],
                _ => [values.Count]
            };
        }

        private double Single(IReadOnlyList<double> values, string name) => values.Count == 1
            ? values[0]
            : throw Error($"{name}() expects exactly one value");

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (!Accept(expected))
                throw Error($"'{expected}' expected");
        }

        private bool Accept(char expected)
        {
            if (position < text.Length && text[position] == expected)
            {
                position++;
                return true;
            }
            return false;
        }

        private void SkipWhitespace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }

        private FormulaException Error(string message) =>
            new($"{message} at position {position + 1} in formula '{text}'.");
    }
}
