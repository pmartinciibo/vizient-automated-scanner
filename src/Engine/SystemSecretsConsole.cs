using System.Security.Principal;
using System.Text;
using Vizient.AutomatedScanner.Core.Secrets;

namespace Vizient.AutomatedScanner.Engine;

/// <summary>The real terminal for the secrets commands.</summary>
internal sealed class SystemSecretsConsole : ISecretsConsole
{
    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;

    public string CurrentAccount =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;

    /// <summary>Reads what is typed without echoing it. Null when input is piped, since there is nobody to ask.</summary>
    public string? ReadSecretWithoutEcho(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Write(prompt);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return typed.ToString();
    }

    public string? ReadStdinLine() => Console.In.ReadLine();
}
