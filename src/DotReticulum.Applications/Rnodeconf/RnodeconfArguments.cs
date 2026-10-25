namespace DotReticulum.Applications.Rnodeconf;

internal static class RnodeconfArguments
{
    internal static string[] Normalize(string[] args)
    {
        const string valueShort = "wtDRH";
        var result = new List<string>();
        var positionalOnly = false;
        foreach (var argument in args)
        {
            if (positionalOnly) result.Add(argument);
            else if (argument == "--") { result.Add(argument); positionalOnly = true; }
            else if (argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('='))
            {
                var position = argument.IndexOf('=');
                result.Add(argument[..position]);
                result.Add(argument[(position + 1)..]);
            }
            else if (argument.Length > 2 && argument[0] == '-' && argument[1] != '-')
            {
                if (char.IsDigit(argument[1])) { result.Add(argument); continue; }
                for (var index = 1; index < argument.Length; index++)
                {
                    var character = argument[index];
                    result.Add("-" + character);
                    if (valueShort.Contains(character))
                    {
                        if (index + 1 < argument.Length) result.Add(argument[(index + 1)..]);
                        break;
                    }
                }
            }
            else result.Add(argument);
        }
        return result.ToArray();
    }
}
