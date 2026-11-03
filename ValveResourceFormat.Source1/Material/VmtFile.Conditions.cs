namespace ValveResourceFormat.Source1.Material;

internal sealed partial class VmtFile
{
    private sealed class Condition(string expression, LoadContext context)
    {
        private int position;
        private string? error;

        public bool Evaluate()
        {
            var value = ReadExpression(0);
            SkipWhiteSpace();

            if (position != expression.Length)
            {
                error ??= "Unexpected token.";
            }

            return error is null && value;
        }

        private void SkipWhiteSpace()
        {
            while (position < expression.Length && char.IsWhiteSpace(expression[position]))
            {
                position++;
            }
        }

        private bool Consume(string token)
        {
            SkipWhiteSpace();

            if (!expression.AsSpan(position).StartsWith(token.AsSpan(), StringComparison.Ordinal))
            {
                return false;
            }

            position += token.Length;
            return true;
        }

        private bool ReadExpression(int depth)
        {
            var value = ReadTerm(depth);

            // Left to right at equal precedence
            while (error is null)
            {
                if (Consume("&&"))
                {
                    value &= ReadTerm(depth);
                }
                else if (Consume("||"))
                {
                    value |= ReadTerm(depth);
                }
                else
                {
                    break;
                }
            }

            return value;
        }

        private bool ReadTerm(int depth)
        {
            if (depth >= 64)
            {
                error = "Expression nesting limit exceeded.";
                return false;
            }

            if (Consume("!"))
            {
                return !ReadTerm(depth + 1);
            }

            if (Consume("("))
            {
                var value = ReadExpression(depth + 1);

                if (!Consume(")"))
                {
                    error ??= "Missing closing parenthesis.";
                }

                return value;
            }

            SkipWhiteSpace();

            if (position < expression.Length && expression[position] == '$')
            {
                var start = ++position;

                while (position < expression.Length && IsSymbolCharacter(expression[position]))
                {
                    position++;
                }

                if (position == start)
                {
                    error = "Expected a symbol after '$'.";
                    return false;
                }

                return context.Resolve(expression[start..position]);
            }

            if (position < expression.Length && char.IsAsciiDigit(expression[position]))
            {
                var value = false;

                while (position < expression.Length && char.IsAsciiDigit(expression[position]))
                {
                    value |= expression[position++] != '0';
                }

                return value;
            }

            error ??= "Expected a symbol, integer, '!' or parenthesis.";
            return false;
        }

        private static bool IsSymbolCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '<' or '>' or '=';
    }
}
