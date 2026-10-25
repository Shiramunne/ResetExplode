using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>
/// Controlador da aba RESET DIAGNÓSTICO (Linha do Tempo de Eventos e Telas Azuis / Falhas).
/// </summary>
public partial class MainWindow
{
    private List<DiagnosticEvent> _allTimelineEvents = new();
    private List<CrashReport> _allCrashReports = new();
    private List<MinidumpFile> _allMinidumps = new();

    private bool _eventsBusy;
    private bool _bsodBusy;
    private string _currentSeverityFilter = "ALL"; // ALL, ERRORS, WARNINGS

    // =========================================================================
    // LINHA DO TEMPO (PageEvents)
    // =========================================================================

    public async Task RefreshEventsTimelineAsync()
    {
        if (_eventsBusy) return;
        _eventsBusy = true;
        BtnEventsRefresh.IsEnabled = false;
        EventsStatus.Text = "Lendo eventos do Windows em segundo plano...";

        try
        {
            _allTimelineEvents = await EventDiagnostics.GetRecentEventsAsync(200);
            ApplyEventsFilter();

            int errors = _allTimelineEvents.Count(e => e.Level is 1 or 2);
            int warnings = _allTimelineEvents.Count(e => e.Level == 3);

            EventsStatus.Text = _allTimelineEvents.Count == 0
                ? "Nenhum evento registrado recentemente."
                : $"{_allTimelineEvents.Count} eventos lidos · {errors} erros/críticos · {warnings} avisos · atualizado às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            EventsStatus.Text = "Erro ao ler eventos: " + ex.Message;
        }
        finally
        {
            _eventsBusy = false;
            BtnEventsRefresh.IsEnabled = true;
        }
    }

    private void ApplyEventsFilter()
    {
        string query = TxtEventSearch?.Text?.Trim().ToLowerInvariant() ?? "";
        IEnumerable<DiagnosticEvent> filtered = _allTimelineEvents;

        // Filtro por Severidade
        if (_currentSeverityFilter == "ERRORS")
        {
            filtered = filtered.Where(e => e.Level is 1 or 2);
        }
        else if (_currentSeverityFilter == "WARNINGS")
        {
            filtered = filtered.Where(e => e.Level == 3);
        }

        // Filtro por Busca de Texto
        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(e =>
                e.Title.ToLowerInvariant().Contains(query) ||
                e.Summary.ToLowerInvariant().Contains(query) ||
                e.ProviderName.ToLowerInvariant().Contains(query) ||
                e.Category.ToLowerInvariant().Contains(query) ||
                e.EventId.ToString().Contains(query) ||
                (!string.IsNullOrEmpty(e.Recommendation) && e.Recommendation.ToLowerInvariant().Contains(query)));
        }

        var resultList = filtered.ToList();
        EventsList.ItemsSource = resultList;

        if (EventsEmptyNotice != null)
        {
            EventsEmptyNotice.Visibility = resultList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void BtnEventsRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEventsTimelineAsync();
    }

    private void TxtEventSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyEventsFilter();
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        _currentSeverityFilter = "ALL";
        HighlightSeverityButton(BtnFilterAll, BtnFilterErrors, BtnFilterWarnings);
        ApplyEventsFilter();
    }

    private void FilterErrors_Click(object sender, RoutedEventArgs e)
    {
        _currentSeverityFilter = "ERRORS";
        HighlightSeverityButton(BtnFilterErrors, BtnFilterAll, BtnFilterWarnings);
        ApplyEventsFilter();
    }

    private void FilterWarnings_Click(object sender, RoutedEventArgs e)
    {
        _currentSeverityFilter = "WARNINGS";
        HighlightSeverityButton(BtnFilterWarnings, BtnFilterAll, BtnFilterErrors);
        ApplyEventsFilter();
    }

    private void HighlightSeverityButton(Button active, Button inactive1, Button inactive2)
    {
        active.Opacity = 1.0;
        active.FontWeight = FontWeights.Bold;
        inactive1.Opacity = 0.6;
        inactive1.FontWeight = FontWeights.Normal;
        inactive2.Opacity = 0.6;
        inactive2.FontWeight = FontWeights.Normal;
    }

    private void BtnCopySelectedEvent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiagnosticEvent ev })
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RESET DIAGNÓSTICO · EVENTO DO WINDOWS]");
            sb.AppendLine($"Título: {ev.Title}");
            sb.AppendLine($"Gravidade: {ev.SeverityText} (Nível {ev.Level})");
            sb.AppendLine($"Categoria: {ev.Category}");
            sb.AppendLine($"Provedor: {ev.ProviderName} (ID: {ev.EventId})");
            sb.AppendLine($"Canal: {ev.Channel}");
            sb.AppendLine($"Data/Hora: {ev.TimeFormatted} ({ev.RelativeTime})");
            sb.AppendLine($"Resumo: {ev.Summary}");
            if (ev.HasRecommendation)
            {
                sb.AppendLine($"Recomendação: {ev.Recommendation}");
            }
            if (!string.IsNullOrWhiteSpace(ev.RawMessage))
            {
                sb.AppendLine($"\nDetalhes Brutos:\n{ev.RawMessage}");
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                Toast("Copiado!", "Detalhes do evento copiados para a área de transferência.", "\uE8C8");
            }
            catch { }
        }
    }

    // =========================================================================
    // TELAS AZUIS E FALHAS (PageBsod)
    // =========================================================================

    public async Task RefreshBsodHistoryAsync()
    {
        if (_bsodBusy) return;
        _bsodBusy = true;
        BtnBsodRefresh.IsEnabled = false;
        BsodStatus.Text = "Analisando histórico de falhas e minidumps...";

        try
        {
            _allCrashReports = await EventDiagnostics.GetCrashReportsAsync(50);
            _allMinidumps = await Task.Run(EventDiagnostics.GetMinidumpFiles);

            BsodList.ItemsSource = _allCrashReports;
            MinidumpList.ItemsSource = _allMinidumps;

            int bsodCount = _allCrashReports.Count(r => r.Type.StartsWith("Tela Azul"));
            int appCrashCount = _allCrashReports.Count(r => r.Type.StartsWith("Travamento"));

            BsodStatus.Text = _allCrashReports.Count == 0
                ? "Nenhuma tela azul ou falha crítica registrada no sistema!"
                : $"{_allCrashReports.Count} falhas registradas ({bsodCount} telas azuis · {appCrashCount} falhas de apps) · {_allMinidumps.Count} minidumps";

            if (BsodEmptyNotice != null)
            {
                BsodEmptyNotice.Visibility = _allCrashReports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (MinidumpSection != null)
            {
                MinidumpSection.Visibility = _allMinidumps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            BsodStatus.Text = "Erro ao analisar falhas: " + ex.Message;
        }
        finally
        {
            _bsodBusy = false;
            BtnBsodRefresh.IsEnabled = true;
        }
    }

    private async void BtnBsodRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBsodHistoryAsync();
    }

    private void BtnOpenMinidumpFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string minidumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (!Directory.Exists(minidumpDir))
            {
                minidumpDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = minidumpDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Toast("Aviso", "Não foi possível abrir a pasta: " + ex.Message, "\uE783");
        }
    }

    private void BtnCopyCrashReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CrashReport r })
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RESET DIAGNÓSTICO · RELATÓRIO DE FALHA]");
            sb.AppendLine($"Tipo: {r.Type}");
            sb.AppendLine($"Data/Hora: {r.TimeFormatted} ({r.RelativeTime})");
            if (!string.IsNullOrEmpty(r.StopCodeName))
            {
                sb.AppendLine($"Código: {r.StopCodeName} ({r.StopCodeHex})");
            }
            sb.AppendLine($"Causa Provável: {r.CulpritCategory}");
            if (!string.IsNullOrEmpty(r.FaultingApp))
            {
                sb.AppendLine($"Aplicativo Afetado: {r.FaultingApp}");
            }
            if (!string.IsNullOrEmpty(r.FaultingModule))
            {
                sb.AppendLine($"Módulo com Falha: {r.FaultingModule}");
            }
            sb.AppendLine($"Diagnóstico: {r.Explanation}");
            if (!string.IsNullOrEmpty(r.Recommendation))
            {
                sb.AppendLine($"Recomendação: {r.Recommendation}");
            }
            if (!string.IsNullOrWhiteSpace(r.RawDetails))
            {
                sb.AppendLine($"\nDetalhes Técnicos:\n{r.RawDetails}");
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                Toast("Copiado!", "Diagnóstico da falha copiado para a área de transferência.", "\uE8C8");
            }
            catch { }
        }
    }
}
