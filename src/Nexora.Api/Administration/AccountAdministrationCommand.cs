using Nexora.Application.Authentication;

namespace Nexora.Api.Administration;

internal static class AccountAdministrationCommand
{
    internal static async Task<int> RunAsync(IServiceProvider services, string command)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Abra um terminal interativo para informar a senha sem eco.");
            return 1;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            string? email = null;
            if (command == "bootstrap-admin")
            {
                Console.Write("Email do administrador: ");
                email = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(char.IsControl))
                {
                    Console.Error.WriteLine("Email inválido.");
                    return 1;
                }
            }
            Console.Write("Nova senha (14–256 caracteres): ");
            var password = ReadPassword(cancellation.Token);
            Console.Write("Confirme a senha: ");
            var confirmation = ReadPassword(cancellation.Token);
            if (password.Length is < 14 or > 256 || !string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("A senha deve ter 14–256 caracteres e a confirmação deve coincidir.");
                return 1;
            }
            await using var scope = services.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<IAccountAdministration>();
            var result = command == "bootstrap-admin"
                ? await administration.BootstrapAsync(email!, password, cancellation.Token)
                : await administration.ResetAdministratorPasswordAsync(password, cancellation.Token);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine(result.Failure switch
                {
                    AuthenticationFailure.AlreadyInitialized => "O bootstrap foi recusado: a conta já foi inicializada.",
                    AuthenticationFailure.AdministratorNotFound => "Não existe uma conta administrativa para recuperar.",
                    AuthenticationFailure.PasswordRejected => "A senha ou o email não atendem aos requisitos.",
                    _ => "Não foi possível concluir a operação."
                });
                return 1;
            }
            Console.WriteLine(command == "bootstrap-admin"
                ? "Administrador criado. Nenhum endpoint de cadastro público foi habilitado."
                : "Senha alterada. Todas as sessões anteriores foram revogadas.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operação cancelada.");
            return 1;
        }
        catch (Exception exception)
        {
            // Do not print provider exceptions, input, connection strings or credentials.
            services.GetRequiredService<ILoggerFactory>().CreateLogger("AccountAdministration")
                .LogWarning(new EventId(2202, "AdministrationFailed"),
                    "Local administration failed. Type {ExceptionType}", exception.GetType().Name);
            Console.Error.WriteLine("Operação não concluída. Verifique a configuração, PostgreSQL e as migrations explícitas.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string ReadPassword(CancellationToken cancellationToken)
    {
        var characters = new List<char>(256);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(characters.ToArray());
            }
            if (key.Key == ConsoleKey.Escape)
            {
                throw new OperationCanceledException();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                if (characters.Count == 256)
                {
                    Console.WriteLine();
                    throw new InvalidOperationException("Senha excede o limite.");
                }
                characters.Add(key.KeyChar);
            }
        }
    }
}
