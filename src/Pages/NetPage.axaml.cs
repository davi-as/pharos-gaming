using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Página Rede: diagnóstico de lag para jogos online.</summary>
public partial class NetPage : UserControl
{
    private CancellationTokenSource? _cts;

    public NetPage()
    {
        InitializeComponent();
        if (NetworkTest.Last != null) Cards.ItemsSource = NetworkTest.Last;
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        BtnTest.Content = "CANCELAR";
        Cards.ItemsSource = null;
        Progress.Value = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.UIThread.Post(() =>
            {
                Status.Text = text;
                Progress.Value = Math.Clamp(frac, 0, 1);
            }), _cts.Token);
            Cards.ItemsSource = cards;
            Status.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
        }
        catch (OperationCanceledException) { Status.Text = "Teste cancelado."; Progress.Value = 0; }
        catch (Exception ex) { Status.Text = "Falhou: " + ex.Message; Progress.Value = 0; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BtnTest.Content = "TESTAR";
        }
    }
}
