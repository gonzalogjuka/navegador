using System.Windows;
using Navegador.Api;

namespace Navegador;

/// <summary>
/// Permisos de las apps locales que usan la API (fase 6.2): la barra azul debajo de la barra de direcciones pregunta
/// "«App» quiere conectarse… Permitir / Rechazar". Un pedido a la vez; los siguientes esperan su turno.
/// </summary>
public partial class MainWindow
{
    private TaskCompletionSource<bool>? _approval;
    private readonly SemaphoreSlim _approvalTurn = new(1, 1);

    internal async Task<bool> AskApprovalAsync(string appName, IReadOnlyList<string> scopes)
    {
        await _approvalTurn.WaitAsync();
        try
        {
            var what = string.Join("; ", scopes.Select(s => ApiServer.ScopeDescriptions.TryGetValue(s, out var d) ? d : s));
            ApprovalText.Text = $"«{appName}», una app de esta PC, quiere conectarse al navegador para: {what}.";
            ApprovalBar.Visibility = Visibility.Visible;
            _approval = new TaskCompletionSource<bool>();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            var allowed = await _approval.Task;
            _log.Event("api_approval", ("app", appName), ("allowed", allowed));
            return allowed;
        }
        finally
        {
            _approval = null;
            ApprovalBar.Visibility = Visibility.Collapsed;
            _approvalTurn.Release();
        }
    }

    private void AnswerApproval(bool allowed) => _approval?.TrySetResult(allowed);

    private void ApprovalAllow_Click(object sender, RoutedEventArgs e) => AnswerApproval(true);

    private void ApprovalDeny_Click(object sender, RoutedEventArgs e) => AnswerApproval(false);

    private void ForgetApps()
    {
        if (_app.Api is not { } api) return;
        if (_app.AutomationScript is null && MessageBox.Show(this,
                $"¿Olvidar las {api.GrantCount} apps conectadas? Se desconectan y la próxima vez vuelven a pedir permiso.",
                "Navegador", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        api.RevokeAll();
    }
}
