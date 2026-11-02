using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Aba G15 CONTROL: unifica o controle de GPU (Undervolt / Curva V-F), CPU Ryzen 7 5800H (SMU / UXTU),
/// Refrigeração Adaptativa + G-Mode (AlienFan) e Iluminação do Teclado 4 Zonas (AlienFX).
/// </summary>
public partial class MainWindow
{
    private const string AlienToolsDir = @"C:\Users\yuria\AppData\Local\AlienFX Tools";
    private const string AlienFanCliPath = @"C:\Users\yuria\AppData\Local\AlienFX Tools\alienfan-cli.exe";
    private const string AlienFxCliPath = @"C:\Users\yuria\AppData\Local\AlienFX Tools\alienfx-cli.exe";
    private const string AfterburnerDir = @"C:\Program Files (x86)\MSI Afterburner";
    private const string AfterburnerExePath = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe";
    private const string UxtuDir = @"C:\Program Files\JamesCJ60\Universal x86 Tuning Utility";
    private const string UxtuDllPath = @"C:\Program Files\JamesCJ60\Universal x86 Tuning Utility\Universal x86 Tuning Utility.dll";
    private const string ScheduledTaskName = "AraujoControlSuite";

    // Curva progressiva calibrada de 8 pontos: (Temp °C, Boost %)
    private static readonly (int Temp, int Boost)[] AdaptiveCurvePoints =
    {
        (0, 0), (50, 0), (58, 15), (68, 35), (75, 55), (82, 80), (87, 100), (100, 100)
    };

    private bool _g15UiInitializing = true;
    private bool _g15Polling;
    private DateTime _lastG15Poll = DateTime.MinValue;
    private int _lastAppliedBoost = -1;

    private void InitG15()
    {
        _g15UiInitializing = true;
        try
        {
            var cfg = _settings.G15;

            // 1. GPU Profile
            RbG15GpuP1.IsChecked = cfg.ActiveGpuProfile != 2;
            RbG15GpuP2.IsChecked = cfg.ActiveGpuProfile == 2;
            SyncG15GpuSlidersToProfile(cfg.ActiveGpuProfile);

            // 2. CPU Ryzen SMU
            G15CpuTempSlider.Value = cfg.ApuTempLimit;
            G15CpuStapmSlider.Value = cfg.ApuStapmWatts;
            G15CpuSlowSlider.Value = cfg.ApuSlowWatts;
            G15CpuFastSlider.Value = cfg.ApuFastWatts;
            UpdateG15CpuLabels();

            // 3. Refrigeração / G-Mode
            RbG15FanAdaptive.IsChecked = !cfg.GModeEnabled;
            RbG15FanGMode.IsChecked = cfg.GModeEnabled;
            UpdateG15QuickProfileBadge();

            // 4. Iluminação do Teclado
            G15KbHexBox.Text = string.IsNullOrWhiteSpace(cfg.KbColorHex) ? "#00A3FF" : cfg.KbColorHex;
            G15KbBrightSlider.Value = Math.Clamp(cfg.KbBrightness, 0, 100);
            UpdateG15KbPreview();

            // 5. Inicialização e Automação
            if (cfg.StartWithWindows)
            {
                SetG15ScheduledTask(true, cfg.StartMinimized);
            }
            ChkG15Startup.IsChecked = IsG15ScheduledTaskRegistered();
            ChkG15StartMin.IsChecked = cfg.StartMinimized;
            ChkG15AutoApply.IsChecked = cfg.AutoApplyOnStartup;
        }
        finally
        {
            _g15UiInitializing = false;
        }

        // Dispara leitura inicial e auto-aplicação silenciosa se configurado
        _ = RefreshG15SensorsAsync();
        if (_settings.G15.AutoApplyOnStartup)
        {
            _ = ApplyAllG15Async(silent: true);
        }
    }

    private void SyncG15GpuSlidersToProfile(int profile)
    {
        if (G15MemSlider == null || G15VoltSlider == null || G15CoreSlider == null || G15CoreLabelTitle == null) return;
        bool prev = _g15UiInitializing;
        _g15UiInitializing = true;
        try
        {
            var cfg = _settings.G15;
            if (profile == 1)
            {
                G15MemSlider.Value = cfg.P1MemClockMhz;
                G15VoltSlider.Value = cfg.P1VoltageMv;
                G15CoreLabelTitle.Text = "CLOCK ALVO NA CURVA (MHz)";
                G15CoreSlider.Minimum = 1500;
                G15CoreSlider.Maximum = 2100;
                G15CoreSlider.TickFrequency = 15;
                G15CoreSlider.Value = cfg.P1CoreClockMhz;
            }
            else
            {
                G15MemSlider.Value = cfg.P2MemClockMhz;
                G15VoltSlider.Value = cfg.P2VoltageMv;
                G15CoreLabelTitle.Text = "OFFSET DA CURVA / CORE (MHz)";
                G15CoreSlider.Minimum = 0;
                G15CoreSlider.Maximum = 1200;
                G15CoreSlider.TickFrequency = 25;
                G15CoreSlider.Value = cfg.P2CoreOffsetMhz;
            }
            UpdateG15GpuLabels();
            UpdateG15QuickProfileBadge();
        }
        finally
        {
            _g15UiInitializing = prev;
        }
    }

    private void UpdateG15GpuLabels()
    {
        if (G15MemValue == null || G15VoltValue == null || G15CoreValue == null || G15BarCurveSummary == null) return;

        int mem = (int)Math.Round(G15MemSlider.Value);
        int mv = (int)Math.Round(G15VoltSlider.Value);
        int core = (int)Math.Round(G15CoreSlider.Value);
        int profile = RbG15GpuP2?.IsChecked == true ? 2 : 1;

        G15MemValue.Text = $"{(mem >= 0 ? "+" : "")}{mem} MHz";
        G15VoltValue.Text = $"{mv} mV";

        if (profile == 1)
        {
            G15CoreValue.Text = $"{core} MHz";
            G15BarCurveSummary.Text = $"{mv} mV · {core} MHz";
        }
        else
        {
            G15CoreValue.Text = $"+{core} MHz";
            G15BarCurveSummary.Text = $"{mv} mV · +{core} MHz";
        }
    }

    private void UpdateG15QuickProfileBadge()
    {
        if (G15LiveGpuProfile == null) return;
        var cfg = _settings.G15;
        if (cfg.ActiveGpuProfile == 1)
        {
            G15LiveGpuProfile.Text = $"PERFIL 1 · {cfg.P1VoltageMv} mV";
            G15LiveGpuSub.Text = $"{cfg.P1CoreClockMhz} MHz · VRAM {(cfg.P1MemClockMhz >= 0 ? "+" : "")}{cfg.P1MemClockMhz} MHz";
        }
        else
        {
            G15LiveGpuProfile.Text = $"PERFIL 2 · {cfg.P2VoltageMv} mV";
            G15LiveGpuSub.Text = $"Offset +{cfg.P2CoreOffsetMhz} MHz · VRAM {(cfg.P2MemClockMhz >= 0 ? "+" : "")}{cfg.P2MemClockMhz} MHz";
        }
    }

    private void G15GpuProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (_g15UiInitializing) return;
        int profile = sender == RbG15GpuP2 ? 2 : 1;
        _settings.G15.ActiveGpuProfile = profile;
        SyncG15GpuSlidersToProfile(profile);
        _settings.Save();
    }

    private void G15GpuSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_g15UiInitializing) return;
        var cfg = _settings.G15;
        int profile = RbG15GpuP2?.IsChecked == true ? 2 : 1;

        int mem = (int)Math.Round(G15MemSlider.Value);
        int mv = (int)Math.Round(G15VoltSlider.Value);
        int core = (int)Math.Round(G15CoreSlider.Value);

        if (profile == 1)
        {
            cfg.P1MemClockMhz = mem;
            cfg.P1VoltageMv = mv;
            cfg.P1CoreClockMhz = core;
        }
        else
        {
            cfg.P2MemClockMhz = mem;
            cfg.P2VoltageMv = mv;
            cfg.P2CoreOffsetMhz = core;
        }
        UpdateG15GpuLabels();
    }

    private async void BtnG15GpuApply_Click(object sender, RoutedEventArgs e)
    {
        BtnG15GpuApply.IsEnabled = false;
        G15GpuStatus.Text = "Aplicando curva e clocks na RTX 3060…";
        try
        {
            _settings.Save();
            string msg = await Task.Run(() => ApplyG15GpuProfileInternal(_settings.G15));
            UpdateG15QuickProfileBadge();
            G15GpuStatus.Text = msg;
        }
        catch (Exception ex)
        {
            G15GpuStatus.Text = $"Erro ao aplicar GPU: {ex.Message}";
        }
        finally
        {
            BtnG15GpuApply.IsEnabled = true;
        }
    }

    private void BtnG15GpuReset_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _settings.G15;
        if (cfg.ActiveGpuProfile == 1)
        {
            cfg.P1MemClockMhz = 500;
            cfg.P1VoltageMv = 850;
            cfg.P1CoreClockMhz = 1860;
        }
        else
        {
            cfg.P2MemClockMhz = 0;
            cfg.P2VoltageMv = 1000;
            cfg.P2CoreOffsetMhz = 950;
        }
        SyncG15GpuSlidersToProfile(cfg.ActiveGpuProfile);
        _settings.Save();
        G15GpuStatus.Text = $"Valores padrão do Perfil {cfg.ActiveGpuProfile} restaurados. Clique em APLICAR CURVA GPU para confirmar.";
    }

    private static string ApplyG15GpuProfileInternal(G15Settings cfg)
    {
        int profile = cfg.ActiveGpuProfile == 2 ? 2 : 1;
        int memMhz = profile == 1 ? cfg.P1MemClockMhz : cfg.P2MemClockMhz;
        int voltMv = profile == 1 ? cfg.P1VoltageMv : cfg.P2VoltageMv;
        int coreVal = profile == 1 ? cfg.P1CoreClockMhz : cfg.P2CoreOffsetMhz;

        bool abUpdated = false;
        string? gpuCfgPath = FindAfterburnerGpuCfg();
        if (gpuCfgPath != null && File.Exists(gpuCfgPath))
        {
            UpdateAfterburnerCfgFile(gpuCfgPath, profile, memMhz, voltMv, coreVal);
            abUpdated = true;
        }

        if (File.Exists(AfterburnerExePath))
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = AfterburnerExePath,
                Arguments = $"-Profile{profile}",
                WorkingDirectory = AfterburnerDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(4000);
        }

        // Também aplica via NvTuning (NVAPI) se disponível para efeito imediato sem depender de UI
        bool nvApiApplied = TryApplyNvidiaViaUxtuDll(profile, memMhz, voltMv, coreVal);

        if (profile == 1)
        {
            return $"Perfil 1 (Otimizado) ativo: Curva cravada em {coreVal} MHz @ {voltMv} mV · Memória {(memMhz >= 0 ? "+" : "")}{memMhz} MHz" +
                   (abUpdated ? " (Afterburner + NVAPI sincronizados)." : nvApiApplied ? " (aplicado via NVAPI)." : ".");
        }
        return $"Perfil 2 (Compatibilidade) ativo: {voltMv} mV · Offset +{coreVal} MHz · Memória {(memMhz >= 0 ? "+" : "")}{memMhz} MHz" +
               (abUpdated ? " (Afterburner sincronizado)." : ".");
    }

    private static string? FindAfterburnerGpuCfg()
    {
        string profilesDir = Path.Combine(AfterburnerDir, "Profiles");
        if (!Directory.Exists(profilesDir)) return null;
        string specific = Path.Combine(profilesDir, "VEN_10DE&DEV_2560&SUBSYS_0A6E1028&REV_A1&BUS_1&DEV_0&FN_0.cfg");
        if (File.Exists(specific)) return specific;
        return Directory.GetFiles(profilesDir, "VEN_10DE*.cfg").FirstOrDefault();
    }

    private static void UpdateAfterburnerCfgFile(string cfgPath, int profile, int memMhz, int voltMv, int coreVal)
    {
        string text = File.ReadAllText(cfgPath);
        string sectionHeader = $"[Profile{profile}]";
        int secIdx = text.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (secIdx < 0) return;

        int nextSecIdx = text.IndexOf("\n[", secIdx + sectionHeader.Length, StringComparison.Ordinal);
        string secContent = nextSecIdx > 0 ? text.Substring(secIdx, nextSecIdx - secIdx) : text.Substring(secIdx);

        // Decodifica a VFCurve atual para recalcular os offsets com precisão
        var vfMatch = Regex.Match(secContent, @"VFCurve\s*=\s*([0-9A-Fa-f]+)");
        if (vfMatch.Success)
        {
            string hexStr = vfMatch.Groups[1].Value;
            byte[] raw = Convert.FromHexString(hexStr);
            uint count = BitConverter.ToUInt32(raw, 4);
            int maxPts = (int)Math.Min(count, (uint)((raw.Length - 12) / 12));

            for (int i = 0; i < maxPts; i++)
            {
                int off = 12 + i * 12;
                float v = BitConverter.ToSingle(raw, off);
                float baseF = BitConverter.ToSingle(raw, off + 4);
                if (v <= 0 || baseF <= 0) continue;

                float newDelta;
                if (profile == 1)
                {
                    if (v >= voltMv)
                    {
                        // Trava o teto de clock exatamente em coreVal (ex.: 1860 MHz) a partir de voltMv (ex.: 850 mV)
                        newDelta = coreVal - baseF;
                    }
                    else if (v >= 700f)
                    {
                        // Rampa progressiva suave entre 700 mV e voltMv
                        float targetDeltaAtPivot = Math.Max(0f, coreVal - 1665f);
                        float t = (v - 700f) / Math.Max(25f, voltMv - 700f);
                        newDelta = MathF.Round(t * targetDeltaAtPivot);
                    }
                    else
                    {
                        newDelta = 0f;
                    }
                }
                else
                {
                    // Perfil 2: offset uniforme
                    newDelta = coreVal;
                }

                byte[] deltaBytes = BitConverter.GetBytes(newDelta);
                Buffer.BlockCopy(deltaBytes, 0, raw, off + 8, 4);
            }

            string newHex = Convert.ToHexString(raw);
            secContent = Regex.Replace(secContent, @"VFCurve\s*=\s*[0-9A-Fa-f]+", $"VFCurve = {newHex}");
        }

        int coreBoostKhz = profile == 1 ? 0 : coreVal * 1000;
        int memBoostKhz = memMhz * 1000;
        secContent = Regex.Replace(secContent, @"CoreClkBoost\s*=\s*-?\d*", $"CoreClkBoost = {coreBoostKhz}");
        secContent = Regex.Replace(secContent, @"MemClkBoost\s*=\s*-?\d*", $"MemClkBoost = {memBoostKhz}");

        string updatedText = nextSecIdx > 0
            ? text.Substring(0, secIdx) + secContent + text.Substring(nextSecIdx)
            : text.Substring(0, secIdx) + secContent;

        File.WriteAllText(cfgPath, updatedText);
    }

    private static bool TryApplyNvidiaViaUxtuDll(int profile, int memMhz, int voltMv, int coreVal)
    {
        try
        {
            if (!File.Exists(UxtuDllPath)) return false;
            var asm = Assembly.LoadFrom(UxtuDllPath);
            var nvType = asm.GetType("Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA.NvTuning");
            if (nvType == null) return false;

            if (profile == 1)
            {
                var setClocks = nvType.GetMethod("SetClocks", BindingFlags.Public | BindingFlags.Static);
                setClocks?.Invoke(null, new object[] { 0, memMhz, 0 });

                var setUv = nvType.GetMethod("SetUndervoltCurveFromDefault", BindingFlags.Public | BindingFlags.Static);
                setUv?.Invoke(null, new object[] { voltMv, coreVal });
            }
            else
            {
                var resetUv = nvType.GetMethod("ResetVfCurveOffsets", BindingFlags.Public | BindingFlags.Static);
                resetUv?.Invoke(null, null);

                var setClocks = nvType.GetMethod("SetClocks", BindingFlags.Public | BindingFlags.Static);
                setClocks?.Invoke(null, new object[] { coreVal, memMhz, 0 });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- CPU AMD Ryzen 7 5800H (SMU / UXTU) ----------------

    private void G15CpuSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_g15UiInitializing) return;
        var cfg = _settings.G15;
        cfg.ApuTempLimit = (int)Math.Round(G15CpuTempSlider.Value);
        cfg.ApuStapmWatts = (int)Math.Round(G15CpuStapmSlider.Value);
        cfg.ApuSlowWatts = (int)Math.Round(G15CpuSlowSlider.Value);
        cfg.ApuFastWatts = (int)Math.Round(G15CpuFastSlider.Value);
        UpdateG15CpuLabels();
    }

    private void UpdateG15CpuLabels()
    {
        if (G15CpuTempValue == null) return;
        G15CpuTempValue.Text = $"{(int)Math.Round(G15CpuTempSlider.Value)} °C";
        G15CpuStapmValue.Text = $"{(int)Math.Round(G15CpuStapmSlider.Value)} W";
        G15CpuSlowValue.Text = $"{(int)Math.Round(G15CpuSlowSlider.Value)} W";
        G15CpuFastValue.Text = $"{(int)Math.Round(G15CpuFastSlider.Value)} W";
    }

    private async void BtnG15CpuApply_Click(object sender, RoutedEventArgs e)
    {
        BtnG15CpuApply.IsEnabled = false;
        G15CpuStatus.Text = "Enviando limites ao SMU do Ryzen 7 5800H…";
        try
        {
            _settings.Save();
            string msg = await Task.Run(() => ApplyG15CpuLimitsInternal(_settings.G15));
            G15CpuStatus.Text = msg;
        }
        catch (Exception ex)
        {
            G15CpuStatus.Text = $"Erro ao aplicar limites na CPU: {ex.Message}";
        }
        finally
        {
            BtnG15CpuApply.IsEnabled = true;
        }
    }

    private void BtnG15CpuReset_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _settings.G15;
        cfg.ApuTempLimit = 88;
        cfg.ApuStapmWatts = 35;
        cfg.ApuSlowWatts = 35;
        cfg.ApuFastWatts = 42;

        _g15UiInitializing = true;
        G15CpuTempSlider.Value = 88;
        G15CpuStapmSlider.Value = 35;
        G15CpuSlowSlider.Value = 35;
        G15CpuFastSlider.Value = 42;
        _g15UiInitializing = false;

        UpdateG15CpuLabels();
        _settings.Save();
        G15CpuStatus.Text = "Preset recomendado carregado (88 °C · 35W STAPM · 35W Slow · 42W Fast). Clique em APLICAR LIMITES CPU.";
    }

    private static string ApplyG15CpuLimitsInternal(G15Settings cfg)
    {
        bool smuOk = false;
        string prevDir = Environment.CurrentDirectory;
        try
        {
            if (File.Exists(UxtuDllPath))
            {
                Environment.CurrentDirectory = UxtuDir;
                var asm = Assembly.LoadFrom(UxtuDllPath);
                var addrType = asm.GetType("RyzenSmu.Addresses");
                var cmdType = asm.GetType("RyzenSmu.SMUCommands");

                addrType?.GetMethod("Socket_FP6_AM4", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, null);

                var applyMethod = cmdType?.GetMethod("applySettings", BindingFlags.Public | BindingFlags.Static);
                if (applyMethod != null)
                {
                    bool r1 = (bool)(applyMethod.Invoke(null, new object[] { "tctl-temp", (uint)cfg.ApuTempLimit }) ?? false);
                    bool r2 = (bool)(applyMethod.Invoke(null, new object[] { "cHTC-temp", (uint)cfg.ApuTempLimit }) ?? false);
                    bool r3 = (bool)(applyMethod.Invoke(null, new object[] { "stapm-limit", (uint)(cfg.ApuStapmWatts * 1000) }) ?? false);
                    bool r4 = (bool)(applyMethod.Invoke(null, new object[] { "slow-limit", (uint)(cfg.ApuSlowWatts * 1000) }) ?? false);
                    bool r5 = (bool)(applyMethod.Invoke(null, new object[] { "fast-limit", (uint)(cfg.ApuFastWatts * 1000) }) ?? false);
                    smuOk = r1 && r3 && r4 && r5;
                }
            }
        }
        catch
        {
            // Ignora falha parcial e sincroniza apuPresets.json abaixo
        }
        finally
        {
            try { Environment.CurrentDirectory = prevDir; } catch { }
        }

        // Sincroniza também o preset "Teste" no apuPresets.json do UXTU
        try
        {
            string presetsPath = Path.Combine(UxtuDir, "apuPresets.json");
            if (File.Exists(presetsPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(presetsPath))?.AsObject();
                if (root != null && root["Teste"] is JsonObject teste)
                {
                    teste["apuTemp"] = cfg.ApuTempLimit;
                    teste["apuSTAPMPow"] = cfg.ApuStapmWatts;
                    teste["apuSlowPow"] = cfg.ApuSlowWatts;
                    teste["apuFastPow"] = cfg.ApuFastWatts;
                    teste["isApuTemp"] = true;
                    teste["isApuSTAPMPow"] = true;
                    teste["isApuSlowPow"] = true;
                    teste["isApuFastPow"] = true;
                    teste["commandValue"] = $"--UXTUSR=False-False-0,05-0-False --tctl-temp={cfg.ApuTempLimit} --cHTC-temp={cfg.ApuTempLimit} --stapm-limit={cfg.ApuStapmWatts * 1000} --fast-limit={cfg.ApuFastWatts * 1000} --slow-limit={cfg.ApuSlowWatts * 1000} ";
                    File.WriteAllText(presetsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { }

        return smuOk
            ? $"SMU Ryzen 7 5800H atualizado em tempo real: Teto {cfg.ApuTempLimit} °C · STAPM {cfg.ApuStapmWatts} W · Slow {cfg.ApuSlowWatts} W · Fast {cfg.ApuFastWatts} W."
            : $"Preset gravado ({cfg.ApuTempLimit} °C · {cfg.ApuStapmWatts}W / {cfg.ApuFastWatts}W). Verifique se o driver PawnIO está ativo.";
    }

    // ---------------- Refrigeração Dell G15 & G-Mode (AlienFan) ----------------

    private async void RbG15FanMode_Click(object sender, RoutedEventArgs e)
    {
        if (_g15UiInitializing) return;
        bool enableGMode = sender == RbG15FanGMode;
        _settings.G15.GModeEnabled = enableGMode;
        _settings.Save();

        G15FanStatus.Text = enableGMode
            ? "Ativando G-Mode (Level 171 · Turbina 100%)…"
            : "Ativando Modo Manual Adaptativo (Curva de 8 pontos)…";

        try
        {
            string msg = await Task.Run(() => ApplyG15ThermalModeInternal(enableGMode));
            G15FanStatus.Text = msg;
            await RefreshG15SensorsAsync();
        }
        catch (Exception ex)
        {
            G15FanStatus.Text = $"Erro ao alternar modo térmico: {ex.Message}";
        }
    }

    private async void BtnG15FanCalibrate_Click(object sender, RoutedEventArgs e)
    {
        BtnG15FanCalibrate.IsEnabled = false;
        G15FanStatus.Text = "Gravando curva progressiva de 8 pontos e recalibrando modo Manual…";
        try
        {
            _settings.G15.GModeEnabled = false;
            _g15UiInitializing = true;
            RbG15FanAdaptive.IsChecked = true;
            RbG15FanGMode.IsChecked = false;
            _g15UiInitializing = false;
            _settings.Save();

            string msg = await Task.Run(() => ApplyG15ThermalModeInternal(enableGMode: false));
            G15FanStatus.Text = msg;
            await RefreshG15SensorsAsync();
        }
        finally
        {
            BtnG15FanCalibrate.IsEnabled = true;
        }
    }

    private static string ApplyG15ThermalModeInternal(bool enableGMode)
    {
        // Garante que a curva de 8 pontos e o Modo Manual (0) estejam sempre gravados no registro do Alienfan
        WriteAlienFanRegistryCurve();

        if (!File.Exists(AlienFanCliPath))
            return "alienfan-cli.exe não encontrado em AppData\\Local\\AlienFX Tools.";

        if (enableGMode)
        {
            RunCli(AlienFanCliPath, "setgmode=1", AlienToolsDir);
            return "G-Mode ATIVADO (Level 171 · Ventoinhas em 100% / ~5000 RPM).";
        }
        else
        {
            RunCli(AlienFanCliPath, "setgmode=0 setpower=0", AlienToolsDir);
            return "Modo Adaptativo Inteligente ATIVO (Modo Manual 0 · Curva progressiva de 8 pontos em CPU e GPU).";
        }
    }

    private static void WriteAlienFanRegistryCurve()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Alienfan", true);
            if (k != null)
            {
                k.SetValue("LastPowerStage", 0, RegistryValueKind.DWord);
                k.SetValue("KeepSystemMode", 0, RegistryValueKind.DWord);
                k.SetValue("DisableAWCC", 1, RegistryValueKind.DWord);
            }

            byte[] curveBytes = new byte[AdaptiveCurvePoints.Length * 2];
            for (int i = 0; i < AdaptiveCurvePoints.Length; i++)
            {
                curveBytes[i * 2] = (byte)AdaptiveCurvePoints[i].Temp;
                curveBytes[i * 2 + 1] = (byte)AdaptiveCurvePoints[i].Boost;
            }

            using var ks = Registry.CurrentUser.CreateSubKey(@"Software\Alienfan\Sensors", true);
            if (ks != null)
            {
                foreach (string sensorPair in new[] { "Fan-0-257", "Fan-0-262", "Fan-1-257", "Fan-1-262" })
                {
                    ks.SetValue(sensorPair, curveBytes, RegistryValueKind.Binary);
                }
            }
        }
        catch { }
    }

    private static int EvaluateAdaptiveBoost(int tempCelsius)
    {
        if (tempCelsius <= AdaptiveCurvePoints[0].Temp) return AdaptiveCurvePoints[0].Boost;
        for (int i = 0; i < AdaptiveCurvePoints.Length - 1; i++)
        {
            var p0 = AdaptiveCurvePoints[i];
            var p1 = AdaptiveCurvePoints[i + 1];
            if (tempCelsius >= p0.Temp && tempCelsius <= p1.Temp)
            {
                if (p1.Temp == p0.Temp) return p1.Boost;
                double t = (double)(tempCelsius - p0.Temp) / (p1.Temp - p0.Temp);
                return (int)Math.Round(p0.Boost + t * (p1.Boost - p0.Boost));
            }
        }
        return 100;
    }

    /// <summary>Chamado pelo timer global de 1s: atualiza os sensores do G15 e mantém a curva adaptativa autônoma.</summary>
    private void TickG15()
    {
        if (_g15Polling) return;
        double intervalSec = PageG15.IsVisible ? 2.0 : 4.0;
        if ((DateTime.UtcNow - _lastG15Poll).TotalSeconds < intervalSec) return;
        _ = RefreshG15SensorsAsync();
    }

    private async Task RefreshG15SensorsAsync()
    {
        if (_g15Polling || !File.Exists(AlienFanCliPath)) return;
        _g15Polling = true;
        _lastG15Poll = DateTime.UtcNow;

        try
        {
            bool wantGMode = _settings.G15.GModeEnabled;
            int lastBoost = _lastAppliedBoost;

            var data = await Task.Run(() =>
            {
                string output = RunCli(AlienFanCliPath, "rpm getfans getpower gmode temp", AlienToolsDir);
                int cpuRpm = ParseCliInt(output, @"Fan CPU 0 RPM:\s*(\d+)");
                int gpuRpm = ParseCliInt(output, @"Fan GPU 1 RPM:\s*(\d+)");
                int cpuBoost = ParseCliInt(output, @"Fan CPU 0 boost:\s*(\d+)");
                int gpuBoost = ParseCliInt(output, @"Fan GPU 1 boost:\s*(\d+)");
                int cpuTemp = ParseCliInt(output, @"CPU Internal Thermistor:\s*(\d+)");
                int gpuTemp = ParseCliInt(output, @"GPU Internal Thermistor:\s*(\d+)");
                bool gmodeOn = Regex.IsMatch(output, @"G-mode is On", RegexOptions.IgnoreCase);
                string powerMode = Regex.Match(output, @"Power mode:\s*([^\r\n]+)").Groups[1].Value.Trim();

                // Se estamos em Modo Adaptativo e o alienfx-gui não estiver rodando, a própria Araújo Control Suite
                // controla os ventiladores diretamente pela curva de 8 pontos!
                bool alienFxGuiRunning = Process.GetProcessesByName("alienfx-gui").Length > 0 ||
                                         Process.GetProcessesByName("alienfan-gui").Length > 0;

                if (!wantGMode && !gmodeOn && !alienFxGuiRunning && (cpuTemp > 0 || gpuTemp > 0))
                {
                    int maxTemp = Math.Max(cpuTemp, gpuTemp);
                    int targetBoost = EvaluateAdaptiveBoost(maxTemp);
                    if (Math.Abs(targetBoost - lastBoost) >= 3 || Math.Abs(targetBoost - cpuBoost) >= 5)
                    {
                        RunCli(AlienFanCliPath, $"setpower=0 setfans={targetBoost},{targetBoost}", AlienToolsDir);
                        cpuBoost = targetBoost;
                        gpuBoost = targetBoost;
                        lastBoost = targetBoost;
                    }
                }

                return (cpuRpm, gpuRpm, cpuBoost, gpuBoost, cpuTemp, gpuTemp, gmodeOn, powerMode, lastBoost);
            });

            _lastAppliedBoost = data.lastBoost;

            if (data.cpuRpm >= 0)
            {
                G15LiveCpuTemp.Text = data.cpuTemp > 0 ? $"{data.cpuTemp} °C · {data.cpuRpm} RPM" : $"{data.cpuRpm} RPM";
                G15LiveCpuBoost.Text = $"Boost ativo: {data.cpuBoost}%";
            }
            if (data.gpuRpm >= 0)
            {
                G15LiveGpuTemp.Text = data.gpuTemp > 0 ? $"{data.gpuTemp} °C · {data.gpuRpm} RPM" : $"{data.gpuRpm} RPM";
                G15LiveGpuBoost.Text = $"Boost ativo: {data.gpuBoost}%";
            }

            G15LivePowerMode.Text = data.gmodeOn ? "G-MODE · 100%" : "ADAPTATIVO · MANUAL";
            G15LivePowerSub.Text = string.IsNullOrEmpty(data.powerMode)
                ? (data.gmodeOn ? "Level 171 (Força Bruta)" : "Manual (0) · Curva 8 pts")
                : data.powerMode;
        }
        catch { }
        finally
        {
            _g15Polling = false;
        }
    }

    private static int ParseCliInt(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
    }

    private static string RunCli(string exePath, string args, string workingDir)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);
            return stdout;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------- Iluminação do Teclado 4 Zonas (AlienFX) ----------------

    private void G15KbHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_g15UiInitializing) return;
        UpdateG15KbPreview();
    }

    private void G15KbBrightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_g15UiInitializing) return;
        if (G15KbBrightValue != null)
            G15KbBrightValue.Text = $"{(int)Math.Round(G15KbBrightSlider.Value)} %";
        _settings.G15.KbBrightness = (int)Math.Round(G15KbBrightSlider.Value);
    }

    private void G15KbPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            G15KbHexBox.Text = hex;
            UpdateG15KbPreview();
        }
    }

    private void UpdateG15KbPreview()
    {
        if (G15KbColorPreview == null || G15KbHexBox == null) return;
        if (G15KbBrightValue != null && G15KbBrightSlider != null)
            G15KbBrightValue.Text = $"{(int)Math.Round(G15KbBrightSlider.Value)} %";

        string raw = G15KbHexBox.Text.Trim();
        if (!raw.StartsWith('#')) raw = "#" + raw;

        if (TryParseHexColor(raw, out byte r, out byte g, out byte b))
        {
            G15KbColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            _settings.G15.KbColorHex = $"#{r:X2}{g:X2}{b:X2}";
        }
    }

    private static bool TryParseHexColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length != 6) return false;
        return byte.TryParse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
               byte.TryParse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
               byte.TryParse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private async void BtnG15KbApply_Click(object sender, RoutedEventArgs e)
    {
        string raw = G15KbHexBox.Text.Trim();
        if (!TryParseHexColor(raw, out byte r, out byte g, out byte b))
        {
            G15KbStatus.Text = "Código HEX inválido. Use o formato #RRGGBB (ex.: #00A3FF).";
            return;
        }

        int bright = (int)Math.Round(G15KbBrightSlider.Value);
        _settings.G15.KbColorHex = $"#{r:X2}{g:X2}{b:X2}";
        _settings.G15.KbBrightness = bright;
        _settings.Save();

        BtnG15KbApply.IsEnabled = false;
        G15KbStatus.Text = $"Aplicando cor #{r:X2}{g:X2}{b:X2} (Brilho {bright}%) nas 4 zonas do teclado…";

        try
        {
            string msg = await Task.Run(() => ApplyG15KeyboardLightInternal(r, g, b, bright));
            G15KbStatus.Text = msg;
        }
        finally
        {
            BtnG15KbApply.IsEnabled = true;
        }
    }

    private static string ApplyG15KeyboardLightInternal(byte r, byte g, byte b, int brightnessPercent)
    {
        if (!File.Exists(AlienFxCliPath))
            return "alienfx-cli.exe não encontrado em AppData\\Local\\AlienFX Tools.";

        double factor = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        int sr = (int)Math.Round(r * factor);
        int sg = (int)Math.Round(g * factor);
        int sb = (int)Math.Round(b * factor);
        int dimVal = (int)Math.Round(factor * 255);

        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Alienfx_SDK", true);
            k?.SetValue("DevBright#6268_1360", dimVal, RegistryValueKind.DWord);
        }
        catch { }

        RunCli(AlienFxCliPath, $"lowlevel setdim={dimVal} setall={sr},{sg},{sb}", AlienToolsDir);
        return $"Iluminação aplicada nas 4 zonas: #{r:X2}{g:X2}{b:X2} · Brilho {brightnessPercent}% (RGB {sr},{sg},{sb}).";
    }

    // ---------------- Inicialização com o Windows & Aplicar Tudo ----------------

    private void ChkG15Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_g15UiInitializing) return;
        bool enable = ChkG15Startup.IsChecked == true;
        _settings.G15.StartWithWindows = enable;
        _settings.Save();
        try
        {
            SetG15ScheduledTask(enable, _settings.G15.StartMinimized);
            G15AutoStatus.Text = enable
                ? "Tarefa agendada 'AraujoControlSuite' ativada: o aplicativo iniciará junto com o Windows como Administrador."
                : "Inicialização automática com o Windows desativada.";
        }
        catch (Exception ex)
        {
            G15AutoStatus.Text = $"Não foi possível alterar a tarefa agendada: {ex.Message}";
        }
    }

    private void ChkG15Options_Click(object sender, RoutedEventArgs e)
    {
        if (_g15UiInitializing) return;
        _settings.G15.StartMinimized = ChkG15StartMin.IsChecked == true;
        _settings.G15.AutoApplyOnStartup = ChkG15AutoApply.IsChecked == true;
        _settings.Save();

        if (ChkG15Startup.IsChecked == true)
        {
            try { SetG15ScheduledTask(true, _settings.G15.StartMinimized); } catch { }
        }
    }

    private async void BtnG15ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        BtnG15ApplyAll.IsEnabled = false;
        G15AutoStatus.Text = "Aplicando pacote completo G15 (GPU + CPU SMU + Ventoinhas + Teclado)…";
        try
        {
            _settings.Save();
            await ApplyAllG15Async(silent: false);
        }
        finally
        {
            BtnG15ApplyAll.IsEnabled = true;
        }
    }

    private async Task ApplyAllG15Async(bool silent)
    {
        try
        {
            var cfg = _settings.G15;
            var results = await Task.Run(() =>
            {
                string gpuMsg = ApplyG15GpuProfileInternal(cfg);
                string cpuMsg = ApplyG15CpuLimitsInternal(cfg);
                string fanMsg = ApplyG15ThermalModeInternal(cfg.GModeEnabled);
                if (TryParseHexColor(cfg.KbColorHex, out byte r, out byte g, out byte b))
                {
                    ApplyG15KeyboardLightInternal(r, g, b, cfg.KbBrightness);
                }
                return (gpuMsg, cpuMsg, fanMsg);
            });

            if (!silent)
            {
                G15GpuStatus.Text = results.gpuMsg;
                G15CpuStatus.Text = results.cpuMsg;
                G15FanStatus.Text = results.fanMsg;
                G15AutoStatus.Text = $"Pacote G15 aplicado com sucesso às {DateTime.Now:HH:mm:ss}.";
            }
            await RefreshG15SensorsAsync();
        }
        catch (Exception ex)
        {
            if (!silent)
                G15AutoStatus.Text = $"Erro ao aplicar pacote completo: {ex.Message}";
        }
    }

    private static bool IsG15ScheduledTaskRegistered()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{ScheduledTaskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetG15ScheduledTask(bool enable, bool startMinimized)
    {
        if (!enable)
        {
            using var del = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{ScheduledTaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            del?.WaitForExit(3000);
            return;
        }

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath)) return;

        string argFlag = startMinimized ? " --minimized" : "";
        string tr = $"\\\"{exePath}\\\"{argFlag}";

        using var create = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Create /TN \"{ScheduledTaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST /F",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        create?.WaitForExit(4000);
    }
}
