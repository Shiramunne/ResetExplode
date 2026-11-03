using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

    // Curva progressiva turbinada de 8 pontos com leitura direta do DIE real (LHM):
    // Silenciosa em repouso (<=48°C), atinge 100% de boost (~5000 a 5200 RPM) logo aos 74°C no chip real
    // para impedir que o Dell G15 entre em thermal throttling no heatpipe compartilhado.
    private static readonly (int Temp, int Boost)[] AdaptiveCurvePoints =
    {
        (0, 0), (48, 0), (52, 25), (58, 45), (64, 70), (70, 88), (74, 100), (100, 100)
    };

    private bool _g15UiInitializing = true;
    private bool _g15Polling;
    private bool _smuApplying;
    private DateTime _lastG15Poll = DateTime.MinValue;
    private DateTime _lastSmuReapply = DateTime.MinValue;
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

            // 2. CPU Ryzen SMU & Auto-Reaplicação UXTU
            if (cfg.AutoReapplySeconds < 3 || cfg.AutoReapplySeconds > 60) cfg.AutoReapplySeconds = 15;
            G15CpuTempSlider.Value = cfg.ApuTempLimit;
            G15CpuStapmSlider.Value = cfg.ApuStapmWatts;
            G15CpuSlowSlider.Value = cfg.ApuSlowWatts;
            G15CpuFastSlider.Value = cfg.ApuFastWatts;
            ChkG15CpuAutoReapply.IsChecked = cfg.AutoReapplyCpu;
            G15CpuIntervalSlider.Value = cfg.AutoReapplySeconds;
            UpdateG15CpuLabels();

            // 3. Refrigeração / G-Mode
            RbG15FanAdaptive.IsChecked = !cfg.GModeEnabled;
            RbG15FanGMode.IsChecked = cfg.GModeEnabled;
            UpdateG15QuickProfileBadge();

            // 4. Iluminação do Teclado
            G15KbHexBox.Text = string.IsNullOrWhiteSpace(cfg.KbColorHex) ? "#00A3FF" : cfg.KbColorHex;
            G15KbBrightSlider.Value = Math.Clamp(cfg.KbBrightness, 0, 100);
            if (G15KbBrightBox != null) G15KbBrightBox.Text = Math.Clamp(cfg.KbBrightness, 0, 100).ToString();
            UpdateG15KbPreview();

            // 5. Inicialização e Automação
            if (cfg.StartWithWindows && !IsG15ScheduledTaskRegistered())
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
        if (_settings.G15.AutoApplyOnStartup || _settings.G15.AutoReapplyCpu)
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

        if (G15MemBox != null && !G15MemBox.IsKeyboardFocusWithin) G15MemBox.Text = mem.ToString();
        if (G15VoltBox != null && !G15VoltBox.IsKeyboardFocusWithin) G15VoltBox.Text = mv.ToString();
        if (G15CoreBox != null && !G15CoreBox.IsKeyboardFocusWithin) G15CoreBox.Text = core.ToString();

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

    private void G15NumericBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is TextBox tb)
        {
            e.Handled = true;
            if (tb == G15MemBox || tb == G15VoltBox || tb == G15CoreBox)
                CommitG15GpuBox(tb);
            else if (tb == G15KbBrightBox)
                CommitG15KbBrightBox();
            else
                CommitG15CpuBox(tb);

            tb.SelectAll();
        }
    }

    private void G15GpuBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitG15GpuBox(tb);
    }

    private void CommitG15GpuBox(TextBox tb)
    {
        if (_g15UiInitializing) return;
        string clean = Regex.Replace(tb.Text ?? "", @"[^\d\-]", "");
        if (!int.TryParse(clean, out int val))
        {
            UpdateG15GpuLabels();
            return;
        }

        if (tb == G15MemBox)
        {
            val = Math.Clamp(val, (int)G15MemSlider.Minimum, (int)G15MemSlider.Maximum);
            G15MemSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15VoltBox)
        {
            val = Math.Clamp(val, (int)G15VoltSlider.Minimum, (int)G15VoltSlider.Maximum);
            G15VoltSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15CoreBox)
        {
            val = Math.Clamp(val, (int)G15CoreSlider.Minimum, (int)G15CoreSlider.Maximum);
            G15CoreSlider.Value = val;
            tb.Text = val.ToString();
        }
        UpdateG15QuickProfileBadge();
        _settings.Save();
    }

    private async void BtnG15GpuApply_Click(object sender, RoutedEventArgs e)
    {
        if (G15MemBox != null) CommitG15GpuBox(G15MemBox);
        if (G15VoltBox != null) CommitG15GpuBox(G15VoltBox);
        if (G15CoreBox != null) CommitG15GpuBox(G15CoreBox);

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
            cfg.P1MemClockMhz = 0;
            cfg.P1VoltageMv = 800;
            cfg.P1CoreClockMhz = 1785;
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
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvApiQueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiEnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGpuBufferDelegate(IntPtr gpuHandle, [In, Out] byte[] buffer);

    private static string ApplyG15GpuProfileInternal(G15Settings cfg)
    {
        int profile = cfg.ActiveGpuProfile == 2 ? 2 : 1;
        int memMhz = profile == 1 ? cfg.P1MemClockMhz : cfg.P2MemClockMhz;
        int voltMv = profile == 1 ? cfg.P1VoltageMv : cfg.P2VoltageMv;
        int coreVal = profile == 1 ? cfg.P1CoreClockMhz : cfg.P2CoreOffsetMhz;

        // 1. Aplica diretamente no driver da NVIDIA via nvapi64.dll nativo (SetPstates20 + SetClockBoostTable),
        //    SEM precisar abrir ou ter o MSI Afterburner instalado!
        bool nvapiOk = TryApplyNvidiaNativeNvapi(profile, memMhz, voltMv, coreVal, out string nvapiDetail);

        // 2. Opcional: se o arquivo .cfg do Afterburner existir no disco, mantém sincronizado apenas como backup passivo
        try
        {
            string? gpuCfgPath = FindAfterburnerGpuCfg();
            if (gpuCfgPath != null && File.Exists(gpuCfgPath))
            {
                string bakPath = gpuCfgPath + ".bak";
                if (profile == 1 && voltMv == 800 && coreVal == 1785 && memMhz == 0 && File.Exists(bakPath))
                {
                    File.Copy(bakPath, gpuCfgPath, overwrite: true);
                }
                else
                {
                    UpdateAfterburnerCfgFile(gpuCfgPath, profile, memMhz, voltMv, coreVal);
                }
            }
        }
        catch { }

        if (profile == 1)
        {
            return nvapiOk
                ? $"Perfil 1 (Undervolt Frio) aplicado direto no driver NVIDIA (NVAPI nativo): Teto {coreVal} MHz @ {voltMv} mV · VRAM {(memMhz >= 0 ? "+" : "")}{memMhz} MHz."
                : $"Perfil 1 ({coreVal} MHz @ {voltMv} mV): {nvapiDetail}";
        }
        return nvapiOk
            ? $"Perfil 2 (Compatibilidade) aplicado direto no driver NVIDIA (NVAPI nativo): {voltMv} mV · Offset +{coreVal} MHz · VRAM {(memMhz >= 0 ? "+" : "")}{memMhz} MHz."
            : $"Perfil 2 ({voltMv} mV · +{coreVal} MHz): {nvapiDetail}";
    }

    private static bool TryApplyNvidiaNativeNvapi(int profile, int memMhz, int voltMv, int coreVal, out string detail)
    {
        detail = "";
        try
        {
            IntPtr pInit = NvApiQueryInterface(0x0150E828);
            IntPtr pEnum = NvApiQueryInterface(0xE5AC921F);
            IntPtr pGetMask = NvApiQueryInterface(0x507B4B59);
            IntPtr pGetTable = NvApiQueryInterface(0x23F1B133);
            IntPtr pSetTable = NvApiQueryInterface(0x0733E009);
            IntPtr pGetVfp = NvApiQueryInterface(0x21537AD4);
            IntPtr pSetPstates = NvApiQueryInterface(0x0F4DAE6B);

            if (pInit == IntPtr.Zero || pEnum == IntPtr.Zero || pGetMask == IntPtr.Zero ||
                pGetTable == IntPtr.Zero || pSetTable == IntPtr.Zero || pGetVfp == IntPtr.Zero || pSetPstates == IntPtr.Zero)
            {
                detail = "Funções NVAPI de curva V/F não encontradas no driver.";
                return false;
            }

            var nvInit = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(pInit);
            var nvEnum = Marshal.GetDelegateForFunctionPointer<NvApiEnumPhysicalGpusDelegate>(pEnum);
            var getMask = Marshal.GetDelegateForFunctionPointer<NvApiGpuBufferDelegate>(pGetMask);
            var getTable = Marshal.GetDelegateForFunctionPointer<NvApiGpuBufferDelegate>(pGetTable);
            var setTable = Marshal.GetDelegateForFunctionPointer<NvApiGpuBufferDelegate>(pSetTable);
            var getVfp = Marshal.GetDelegateForFunctionPointer<NvApiGpuBufferDelegate>(pGetVfp);
            var setPstates = Marshal.GetDelegateForFunctionPointer<NvApiGpuBufferDelegate>(pSetPstates);

            if (nvInit() != 0)
            {
                detail = "Falha ao inicializar nvapi64.dll.";
                return false;
            }

            IntPtr[] gpus = new IntPtr[64];
            if (nvEnum(gpus, out int gpuCount) != 0 || gpuCount <= 0)
            {
                detail = "Nenhuma GPU física NVIDIA encontrada.";
                return false;
            }
            IntPtr gpu = gpus[0];

            // 1. Lê a máscara de pontos V/F suportados pela GPU (6188 bytes, ver 1)
            byte[] maskBuf = new byte[6188];
            BitConverter.TryWriteBytes(maskBuf.AsSpan(0, 4), (1u << 16) | 6188u);
            if (getMask(gpu, maskBuf) != 0)
            {
                detail = "Falha em NvAPI_GPU_GetClockBoostMask.";
                return false;
            }

            // 2. Zera a tabela de offsets da curva primeiro para medir a curva base limpa
            byte[] tableBuf = new byte[9248];
            BitConverter.TryWriteBytes(tableBuf.AsSpan(0, 4), (1u << 16) | 9248u);
            Buffer.BlockCopy(maskBuf, 4, tableBuf, 4, 32);
            getTable(gpu, tableBuf);
            for (int i = 0; i < 255; i++)
            {
                BitConverter.TryWriteBytes(tableBuf.AsSpan(68 + i * 36 + 20, 4), 0);
            }
            setTable(gpu, tableBuf);

            // 3. Aplica o offset de Memória (VRAM) e Core P0 via SetPstates20 (7416 bytes, ver 2)
            byte[] pstateBuf = new byte[7416];
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(0, 4), (2u << 16) | 7416u);
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(8, 4), 1u);  // numPstates = 1
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(12, 4), 2u); // numClocks = 2
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(20, 4), 0u); // pstateId = P0 (0)

            // Clock 0: Core (domainId = 0)
            int corePstateDeltaKhz = profile == 2 ? coreVal * 1000 : 0;
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(28, 4), 0u);
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(40, 4), corePstateDeltaKhz);

            // Clock 1: Memory (domainId = 4)
            int memPstateDeltaKhz = memMhz * 1000;
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(72, 4), 4u);
            BitConverter.TryWriteBytes(pstateBuf.AsSpan(84, 4), memPstateDeltaKhz);

            int rPstate = setPstates(gpu, pstateBuf);

            // 4. Se Perfil 1 (Undervolt Frio por Curva V/F), reproduz exatamente a geometria térmica original:
            //    - <= 725 mV: mantido em 1267 MHz (ou base se menor)
            //    - 725 mV até voltMv (ex.: 800 mV): rampa linear controlada de 1267 MHz até coreVal (ex.: 1785 MHz)
            //    - >= voltMv: teto plano travado em coreVal (deltas negativos nos pontos altos para impedir aquecimento)
            if (profile == 1)
            {
                byte[] vfpBuf = new byte[7208];
                BitConverter.TryWriteBytes(vfpBuf.AsSpan(0, 4), (1u << 16) | 7208u);
                Buffer.BlockCopy(maskBuf, 4, vfpBuf, 4, 32);
                if (getVfp(gpu, vfpBuf) != 0)
                {
                    detail = "Falha em NvAPI_GPU_GetVFPointsStatus.";
                    return false;
                }

                int targetKhz = coreVal * 1000;
                const int floorKhz = 1267000;

                for (int i = 0; i < 255; i++)
                {
                    uint baseFreqKhz = BitConverter.ToUInt32(vfpBuf, 68 + i * 28 + 4);
                    uint voltUv = BitConverter.ToUInt32(vfpBuf, 68 + i * 28 + 8);
                    if (baseFreqKhz == 0 || voltUv == 0) continue;

                    double ptMv = voltUv / 1000.0;
                    int desiredFreqKhz;
                    if (ptMv >= voltMv)
                    {
                        desiredFreqKhz = targetKhz;
                    }
                    else if (ptMv > 725.0)
                    {
                        double t = (ptMv - 725.0) / Math.Max(15.0, voltMv - 725.0);
                        desiredFreqKhz = (int)Math.Round(floorKhz + t * (targetKhz - floorKhz));
                    }
                    else
                    {
                        desiredFreqKhz = Math.Min((int)baseFreqKhz, floorKhz);
                    }

                    int deltaKhz = desiredFreqKhz - (int)baseFreqKhz;
                    BitConverter.TryWriteBytes(tableBuf.AsSpan(68 + i * 36 + 20, 4), deltaKhz);
                }

                int rTable = setTable(gpu, tableBuf);
                if (rTable != 0)
                {
                    detail = $"SetClockBoostTable retornou código {rTable}.";
                    return false;
                }
            }

            detail = rPstate == 0 ? "OK" : $"SetPstates20={rPstate}";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
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
                        newDelta = coreVal - baseF;
                    }
                    else if (v > 725f)
                    {
                        float t = (v - 725f) / Math.Max(15f, voltMv - 725f);
                        float desiredF = 1267f + t * (coreVal - 1267f);
                        newDelta = MathF.Round(desiredF - baseF);
                    }
                    else
                    {
                        newDelta = Math.Min(0f, 1267f - baseF);
                    }
                }
                else
                {
                    newDelta = coreVal;
                }

                byte[] deltaBytes = BitConverter.GetBytes(newDelta);
                Buffer.BlockCopy(deltaBytes, 0, raw, off + 8, 4);
            }

            string newHex = Convert.ToHexString(raw);
            secContent = Regex.Replace(secContent, @"VFCurve\s*=\s*[0-9A-Fa-f]+", $"VFCurve = {newHex}");
        }

        int coreBoostKhz = profile == 1 ? -502296 : coreVal * 1000;
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
        if (G15CpuIntervalSlider != null)
            cfg.AutoReapplySeconds = Math.Clamp((int)Math.Round(G15CpuIntervalSlider.Value), 3, 60);
        UpdateG15CpuLabels();
    }

    private void ChkG15CpuAutoReapply_Click(object sender, RoutedEventArgs e)
    {
        if (_g15UiInitializing) return;
        _settings.G15.AutoReapplyCpu = ChkG15CpuAutoReapply.IsChecked == true;
        _settings.Save();
        if (_settings.G15.AutoReapplyCpu)
        {
            _lastSmuReapply = DateTime.MinValue; // dispara reaplicação imediata no próximo tick
        }
    }

    private void G15CpuBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitG15CpuBox(tb);
    }

    private void CommitG15CpuBox(TextBox tb)
    {
        if (_g15UiInitializing) return;
        string clean = Regex.Replace(tb.Text ?? "", @"[^\d]", "");
        if (!int.TryParse(clean, out int val))
        {
            UpdateG15CpuLabels();
            return;
        }

        if (tb == G15CpuTempBox)
        {
            val = Math.Clamp(val, (int)G15CpuTempSlider.Minimum, (int)G15CpuTempSlider.Maximum);
            G15CpuTempSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15CpuStapmBox)
        {
            val = Math.Clamp(val, (int)G15CpuStapmSlider.Minimum, (int)G15CpuStapmSlider.Maximum);
            G15CpuStapmSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15CpuSlowBox)
        {
            val = Math.Clamp(val, (int)G15CpuSlowSlider.Minimum, (int)G15CpuSlowSlider.Maximum);
            G15CpuSlowSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15CpuFastBox)
        {
            val = Math.Clamp(val, (int)G15CpuFastSlider.Minimum, (int)G15CpuFastSlider.Maximum);
            G15CpuFastSlider.Value = val;
            tb.Text = val.ToString();
        }
        else if (tb == G15CpuIntervalBox && G15CpuIntervalSlider != null)
        {
            val = Math.Clamp(val, (int)G15CpuIntervalSlider.Minimum, (int)G15CpuIntervalSlider.Maximum);
            G15CpuIntervalSlider.Value = val;
            tb.Text = val.ToString();
        }
        _settings.Save();
    }

    private void UpdateG15CpuLabels()
    {
        if (G15CpuTempValue == null) return;
        int temp = (int)Math.Round(G15CpuTempSlider.Value);
        int stapm = (int)Math.Round(G15CpuStapmSlider.Value);
        int slow = (int)Math.Round(G15CpuSlowSlider.Value);
        int fast = (int)Math.Round(G15CpuFastSlider.Value);
        int interval = G15CpuIntervalSlider != null ? (int)Math.Round(G15CpuIntervalSlider.Value) : _settings.G15.AutoReapplySeconds;

        G15CpuTempValue.Text = $"{temp} °C";
        G15CpuStapmValue.Text = $"{stapm} W";
        G15CpuSlowValue.Text = $"{slow} W";
        G15CpuFastValue.Text = $"{fast} W";

        if (G15CpuTempBox != null && !G15CpuTempBox.IsKeyboardFocusWithin) G15CpuTempBox.Text = temp.ToString();
        if (G15CpuStapmBox != null && !G15CpuStapmBox.IsKeyboardFocusWithin) G15CpuStapmBox.Text = stapm.ToString();
        if (G15CpuSlowBox != null && !G15CpuSlowBox.IsKeyboardFocusWithin) G15CpuSlowBox.Text = slow.ToString();
        if (G15CpuFastBox != null && !G15CpuFastBox.IsKeyboardFocusWithin) G15CpuFastBox.Text = fast.ToString();
        if (G15CpuIntervalBox != null && !G15CpuIntervalBox.IsKeyboardFocusWithin) G15CpuIntervalBox.Text = interval.ToString();
        if (CpuUxtuCapSummary != null) CpuUxtuCapSummary.Text = $"{temp} °C (Teto Custom)";
        if (CpuUxtuReapplyStatus != null) CpuUxtuReapplyStatus.Text = _settings.G15.AutoReapplyCpu ? $"ATIVA ({interval}s)" : "DESLIGADA";
    }

    private async void BtnG15CpuApply_Click(object sender, RoutedEventArgs e)
    {
        if (G15CpuTempBox != null) CommitG15CpuBox(G15CpuTempBox);
        if (G15CpuStapmBox != null) CommitG15CpuBox(G15CpuStapmBox);
        if (G15CpuSlowBox != null) CommitG15CpuBox(G15CpuSlowBox);
        if (G15CpuFastBox != null) CommitG15CpuBox(G15CpuFastBox);
        if (G15CpuIntervalBox != null) CommitG15CpuBox(G15CpuIntervalBox);

        BtnG15CpuApply.IsEnabled = false;
        G15CpuStatus.Text = "Enviando limites ao SMU do Ryzen 7 5800H…";
        try
        {
            _settings.Save();
            _lastSmuReapply = DateTime.UtcNow;
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
        cfg.ApuStapmWatts = 29;
        cfg.ApuSlowWatts = 29;
        cfg.ApuFastWatts = 32;
        cfg.AutoReapplyCpu = true;
        cfg.AutoReapplySeconds = 15;

        _g15UiInitializing = true;
        G15CpuTempSlider.Value = 88;
        G15CpuStapmSlider.Value = 29;
        G15CpuSlowSlider.Value = 29;
        G15CpuFastSlider.Value = 32;
        if (ChkG15CpuAutoReapply != null) ChkG15CpuAutoReapply.IsChecked = true;
        if (G15CpuIntervalSlider != null) G15CpuIntervalSlider.Value = 15;
        _g15UiInitializing = false;

        UpdateG15CpuLabels();
        _settings.Save();
        G15CpuStatus.Text = "Preset frio original carregado (88 °C · 29W STAPM · 29W Slow · 32W Fast · Reaplicação 15s). Clique em APLICAR LIMITES CPU.";
    }

    private static string ApplyG15CpuLimitsInternal(G15Settings cfg)
    {
        bool smuOk = false;
        string smuError = "";
        string prevDir = Environment.CurrentDirectory;
        string logPath = Path.Combine(AppSettings.Folder, "tuning.log");
        var logDetails = new List<string>();

        try
        {
            bool dllFound = File.Exists(UxtuDllPath);
            logDetails.Add($"dllFound={dllFound}");
            if (dllFound)
            {
                Environment.CurrentDirectory = UxtuDir;
                var asm = Assembly.LoadFrom(UxtuDllPath);
                logDetails.Add($"asm={asm != null}");

                // CRÍTICO: UXTU.dll busca "Assets\AMD\PawnIO\RyzenSMU.bin" concatenando Settings.Default.Path.
                // Sem definir Settings.Default.Path = UxtuDir, o PawnIO lança FileNotFoundException silenciosamente!
                var settingsType = asm.GetType("Universal_x86_Tuning_Utility.Properties.Settings");
                var defaultProp = settingsType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                var settingsInst = defaultProp?.GetValue(null);
                var pathProp = settingsType?.GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
                if (settingsInst != null && pathProp != null)
                {
                    pathProp.SetValue(settingsInst, UxtuDir);
                }

                // CRÍTICO: Inicializa Family.setCpuFamily() e Addresses.setAddresses() antes de enviar comandos SMU
                var famType = asm.GetType("Universal_x86_Tuning_Utility.Scripts.Family");
                var addrType = asm.GetType("RyzenSmu.Addresses");
                var cmdType = asm.GetType("RyzenSmu.SMUCommands");

                var famField = famType?.GetField("FAM", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int famVal = famField != null ? Convert.ToInt32(famField.GetValue(null)) : -1;
                if (famVal <= 0)
                {
                    famType?.GetMethod("setCpuFamily", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?.Invoke(null, null);
                }

                addrType?.GetMethod("setAddresses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, null);

                logDetails.Add($"cmdType={cmdType != null}");
                // 1. Aplicação via SMUCommands individuais com tratamento seguro por comando
                var applyMethod = cmdType?.GetMethod("applySettings", BindingFlags.Public | BindingFlags.Static);
                logDetails.Add($"applyMethod={applyMethod != null}");
                if (applyMethod != null)
                {
                    bool rTemp = false, rStapm = false, rSlow = false, rFast = false, rApuSlow = false;
                    try { rTemp = (bool)(applyMethod.Invoke(null, new object[] { "tctl-temp", (uint)cfg.ApuTempLimit }) ?? false); } catch { }
                    try { rStapm = (bool)(applyMethod.Invoke(null, new object[] { "stapm-limit", (uint)(cfg.ApuStapmWatts * 1000) }) ?? false); } catch { }
                    try { rSlow = (bool)(applyMethod.Invoke(null, new object[] { "slow-limit", (uint)(cfg.ApuSlowWatts * 1000) }) ?? false); } catch { }
                    try { rFast = (bool)(applyMethod.Invoke(null, new object[] { "fast-limit", (uint)(cfg.ApuFastWatts * 1000) }) ?? false); } catch { }
                    try { rApuSlow = (bool)(applyMethod.Invoke(null, new object[] { "apu-slow-limit", (uint)(cfg.ApuSlowWatts * 1000) }) ?? false); } catch { }
                    try { applyMethod.Invoke(null, new object[] { "cHTC-temp", (uint)cfg.ApuTempLimit }); } catch { }

                    logDetails.Add($"tctl={rTemp}({cfg.ApuTempLimit}C)");
                    logDetails.Add($"stapm={rStapm}({cfg.ApuStapmWatts}W)");
                    logDetails.Add($"slow={rSlow}({cfg.ApuSlowWatts}W)");
                    logDetails.Add($"fast={rFast}({cfg.ApuFastWatts}W)");
                    logDetails.Add($"apuSlow={rApuSlow}");

                    smuOk = rTemp && rStapm && rSlow && rFast;
                }

                // 2. Aplicação complementar via RyzenAdj_To_UXTU.Translate (motor nativo UXTU com fallback para overlays de energia)
                try
                {
                    var translateType = asm.GetType("Universal_x86_Tuning_Utility.Scripts.RyzenAdj_To_UXTU");
                    logDetails.Add($"translateType={translateType != null}");
                    var translateMethod = translateType?.GetMethod("Translate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    logDetails.Add($"translateMethod={translateMethod != null}");
                    if (translateMethod != null)
                    {
                        string commandStr = $"--UXTUSR=False-False-0,05-0-False --tctl-temp={cfg.ApuTempLimit} --cHTC-temp={cfg.ApuTempLimit} --stapm-limit={cfg.ApuStapmWatts * 1000} --fast-limit={cfg.ApuFastWatts * 1000} --slow-limit={cfg.ApuSlowWatts * 1000} --apu-slow-limit={cfg.ApuSlowWatts * 1000} ";
                        translateMethod.Invoke(null, new object[] { commandStr, false, false, "Custom", false });
                        logDetails.Add("translate=OK");
                        smuOk = true;
                    }
                }
                catch (Exception exTr)
                {
                    logDetails.Add($"translateErr={exTr.InnerException?.Message ?? exTr.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            smuError = ex.InnerException?.Message ?? ex.Message;
            logDetails.Add($"fatalErr={smuError}");
        }
        finally
        {
            try { Environment.CurrentDirectory = prevDir; } catch { }
        }

        // 3. Sincroniza todos os presets no apuPresets.json do UXTU
        try
        {
            string presetsPath = Path.Combine(UxtuDir, "apuPresets.json");
            if (File.Exists(presetsPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(presetsPath))?.AsObject();
                if (root != null)
                {
                    foreach (var kvp in root)
                    {
                        if (kvp.Value is JsonObject p)
                        {
                            p["apuTemp"] = cfg.ApuTempLimit;
                            p["apuSTAPMPow"] = cfg.ApuStapmWatts;
                            p["apuSlowPow"] = cfg.ApuSlowWatts;
                            p["apuFastPow"] = cfg.ApuFastWatts;
                            p["isApuTemp"] = true;
                            p["isApuSTAPMPow"] = true;
                            p["isApuSlowPow"] = true;
                            p["isApuFastPow"] = true;
                            p["commandValue"] = $"--UXTUSR=False-False-0,05-0-False --tctl-temp={cfg.ApuTempLimit} --cHTC-temp={cfg.ApuTempLimit} --stapm-limit={cfg.ApuStapmWatts * 1000} --fast-limit={cfg.ApuFastWatts * 1000} --slow-limit={cfg.ApuSlowWatts * 1000} ";
                        }
                    }
                    File.WriteAllText(presetsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { }

        // 4. Sincroniza o user.config do UXTU (AppData local) para auto-reaplicação contínua de backup
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string uxtuConfigDir = Path.Combine(localAppData, "JamesCJ60");
            if (Directory.Exists(uxtuConfigDir))
            {
                foreach (var cfgFile in Directory.GetFiles(uxtuConfigDir, "user.config", SearchOption.AllDirectories))
                {
                    string xml = File.ReadAllText(cfgFile);
                    string targetCmd = $"--UXTUSR=False-False-0,05-0-False --tctl-temp={cfg.ApuTempLimit} --cHTC-temp={cfg.ApuTempLimit} --stapm-limit={cfg.ApuStapmWatts * 1000} --fast-limit={cfg.ApuFastWatts * 1000} --slow-limit={cfg.ApuSlowWatts * 1000} ";
                    string patched = Regex.Replace(xml, @"(<setting name=""CommandString"" serializeAs=""String"">\s*<value>)[^<]*(</value>)", $"${{1}}{targetCmd}${{2}}");
                    patched = Regex.Replace(patched, @"(<setting name=""AutoReapply"" serializeAs=""String"">\s*<value>)[^<]*(</value>)", "${1}True${2}");
                    patched = Regex.Replace(patched, @"(<setting name=""AutoReapplyTime"" serializeAs=""String"">\s*<value>)[^<]*(</value>)", $"${{1}}{cfg.AutoReapplySeconds}${{2}}");
                    if (patched != xml) File.WriteAllText(cfgFile, patched);
                }
            }
        }
        catch { }

        // 5. Registra no tuning.log para auditoria transparente
        try
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CPU SMU: {string.Join(" | ", logDetails)} (smuOk={smuOk})\n";
            File.AppendAllText(logPath, logLine);
        }
        catch { }

        string autoNote = cfg.AutoReapplyCpu ? $" · Auto-reaplicação ativa ({cfg.AutoReapplySeconds}s)" : " · Auto-reaplicação desligada";
        return smuOk
            ? $"SMU Ryzen 7 5800H travado via PawnIO ({DateTime.Now:HH:mm:ss}): Teto {cfg.ApuTempLimit} °C · STAPM {cfg.ApuStapmWatts} W · Slow {cfg.ApuSlowWatts} W · Fast {cfg.ApuFastWatts} W{autoNote}."
            : $"Falha ao gravar no SMU ({smuError}). Verifique se o aplicativo está rodando como Administrador e se o driver PawnIO está ativo.";
    }

    // ---------------- Refrigeração Dell G15 & G-Mode (AlienFan + Tecla Física G) ----------------

    /// <summary>Acionado quando o usuário aperta o botão físico G-Mode (Fn+F9 / F17) no teclado do Dell G15.</summary>
    public async Task ToggleG15GModeFromPhysicalKeyAsync()
    {
        bool enableGMode = !_settings.G15.GModeEnabled;
        _settings.G15.GModeEnabled = enableGMode;
        _g15UiInitializing = true;
        if (RbG15FanAdaptive != null) RbG15FanAdaptive.IsChecked = !enableGMode;
        if (RbG15FanGMode != null) RbG15FanGMode.IsChecked = enableGMode;
        _g15UiInitializing = false;
        _settings.Save();

        if (G15FanStatus != null)
        {
            G15FanStatus.Text = enableGMode
                ? "Tecla física G pressionada: ativando G-Mode (Level 171 · Turbina 100%)…"
                : "Tecla física G pressionada: voltando ao Modo Adaptativo Inteligente…";
        }

        Toast(
            enableGMode ? "G-Mode ATIVADO (Tecla G)" : "Modo Adaptativo ATIVO (Tecla G)",
            enableGMode ? "Level 171 · Ventoinhas em 100% (~5000 RPM)" : "Curva inteligente de 8 pontos (100% aos 78 °C)",
            "\uE945"
        );

        try
        {
            string msg = await Task.Run(() => ApplyG15ThermalModeInternal(enableGMode));
            if (G15FanStatus != null) G15FanStatus.Text = msg;
            await RefreshG15SensorsAsync();
        }
        catch (Exception ex)
        {
            if (G15FanStatus != null) G15FanStatus.Text = $"Erro ao alternar G-Mode pela tecla física: {ex.Message}";
        }
    }

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

            byte[] cpuCurveBytes = new byte[AdaptiveCurvePoints.Length * 2];
            for (int i = 0; i < AdaptiveCurvePoints.Length; i++)
            {
                cpuCurveBytes[i * 2] = (byte)AdaptiveCurvePoints[i].Temp;
                cpuCurveBytes[i * 2 + 1] = (byte)AdaptiveCurvePoints[i].Boost;
            }

            // O Sensor 262 ("GPU Internal Thermistor" da placa-mãe Dell) mede ~15°C abaixo do die real da RTX 3060.
            // Compensamos a curva do Sensor 262 para atingir 100% aos 65°C do termistor (~80°C no chip da GPU).
            (int Temp, int Boost)[] gpuBoardCurve =
            {
                (0, 0), (40, 0), (46, 30), (52, 60), (58, 85), (60, 100), (100, 100)
            };
            byte[] gpuCurveBytes = new byte[gpuBoardCurve.Length * 2];
            for (int i = 0; i < gpuBoardCurve.Length; i++)
            {
                gpuCurveBytes[i * 2] = (byte)gpuBoardCurve[i].Temp;
                gpuCurveBytes[i * 2 + 1] = (byte)gpuBoardCurve[i].Boost;
            }

            using var ks = Registry.CurrentUser.CreateSubKey(@"Software\Alienfan\Sensors", true);
            if (ks != null)
            {
                ks.SetValue("Fan-0-257", cpuCurveBytes, RegistryValueKind.Binary);
                ks.SetValue("Fan-1-257", cpuCurveBytes, RegistryValueKind.Binary);
                ks.SetValue("Fan-0-262", gpuCurveBytes, RegistryValueKind.Binary);
                ks.SetValue("Fan-1-262", gpuCurveBytes, RegistryValueKind.Binary);
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

    /// <summary>Chamado pelo timer global de 1s: atualiza os sensores do G15, reaplica SMU no intervalo configurado (como no UXTU) e mantém a curva de ventoinhas ativa.</summary>
    private void TickG15()
    {
        // Verifica se há pedido externo para encerrar a aplicação
        try
        {
            string exitFlag = Path.Combine(AppSettings.Folder, "exit.flag");
            if (File.Exists(exitFlag))
            {
                File.Delete(exitFlag);
                _realExit = true;
                Close();
                return;
            }
        }
        catch { }

        int intervalSecSmu = Math.Clamp(_settings.G15.AutoReapplySeconds, 3, 60);
        if (!_smuApplying && _settings.G15.AutoReapplyCpu && (DateTime.UtcNow - _lastSmuReapply).TotalSeconds >= intervalSecSmu)
        {
            _smuApplying = true;
            _lastSmuReapply = DateTime.UtcNow;
            var cfgSnap = _settings.G15;
            _ = Task.Run(() =>
            {
                try
                {
                    string msg = ApplyG15CpuLimitsInternal(cfgSnap);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (G15CpuStatus != null && (PageUxtu?.IsVisible == true || PageAlienFan?.IsVisible == true))
                            G15CpuStatus.Text = msg;
                    }));
                }
                catch { }
                finally { _smuApplying = false; }
            });
        }

        if (_g15Polling) return;
        bool isTuningPageVisible = (PageAlienFan != null && PageAlienFan.IsVisible) ||
                                  (PageAfterburner != null && PageAfterburner.IsVisible) ||
                                  (PageUxtu != null && PageUxtu.IsVisible);
        double intervalSec = isTuningPageVisible ? 2.0 : 3.0;
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

            // Lê a temperatura real do die da CPU (Tctl/Tdie) e da GPU (RTX 3060 Core) direto do LibreHardwareMonitor,
            // pois o "GPU Internal Thermistor" da placa-mãe Dell mede ~18°C abaixo do chip da RTX 3060!
            int lhmCpuTemp = _monitor.CpuTemperature is float ct && ct > 0 ? (int)Math.Round(ct) : -1;
            int lhmGpuTemp = MainGpuTemp() is float gt && gt > 0 ? (int)Math.Round(gt) : -1;

            var data = await Task.Run(() =>
            {
                string output = RunCli(AlienFanCliPath, "rpm getfans getpower gmode temp", AlienToolsDir);
                int cpuRpm = ParseCliInt(output, @"Fan CPU 0 RPM:\s*(\d+)");
                int gpuRpm = ParseCliInt(output, @"Fan GPU 1 RPM:\s*(\d+)");
                int cpuBoost = ParseCliInt(output, @"Fan CPU 0 boost:\s*(\d+)");
                int gpuBoost = ParseCliInt(output, @"Fan GPU 1 boost:\s*(\d+)");
                int cliCpuTemp = ParseCliInt(output, @"CPU Internal Thermistor:\s*(\d+)");
                int cliGpuTemp = ParseCliInt(output, @"GPU Internal Thermistor:\s*(\d+)");
                bool gmodeOn = Regex.IsMatch(output, @"G-mode is On", RegexOptions.IgnoreCase);
                string powerMode = Regex.Match(output, @"Power mode:\s*([^\r\n]+)").Groups[1].Value.Trim();

                int cpuTemp = Math.Max(cliCpuTemp, lhmCpuTemp);
                int gpuTemp = Math.Max(cliGpuTemp, lhmGpuTemp);

                int maxTemp = Math.Max(cpuTemp, gpuTemp);
                if (!wantGMode)
                {
                    // No Modo Dinâmico:
                    // O firmware da Dell trava as ventoinhas em ~4200 RPM em modo manual (setpower=0).
                    // Para atingir 5000-5200 RPM em jogos pesados sem exigir intervenção do usuário:
                    // Se a temperatura do chip atingir 74°C (ou GPU >= 72°C ou CPU >= 78°C), engata o G-Mode no hardware!
                    if (maxTemp >= 74 || gpuTemp >= 72 || cpuTemp >= 78)
                    {
                        if (!gmodeOn)
                        {
                            RunCli(AlienFanCliPath, "setgmode=1", AlienToolsDir);
                            gmodeOn = true;
                            powerMode = "Level 171 (Turbo Dinâmico)";
                        }
                    }
                    else if (maxTemp <= 64 && gpuTemp <= 62 && cpuTemp <= 65)
                    {
                        if (gmodeOn)
                        {
                            RunCli(AlienFanCliPath, "setgmode=0 setpower=0", AlienToolsDir);
                            gmodeOn = false;
                        }
                        int targetBoost = EvaluateAdaptiveBoost(maxTemp);
                        if (Math.Abs(targetBoost - lastBoost) >= 3 || Math.Abs(targetBoost - cpuBoost) >= 5 || Math.Abs(targetBoost - gpuBoost) >= 5)
                        {
                            RunCli(AlienFanCliPath, $"setpower=0 setfans={targetBoost},{targetBoost}", AlienToolsDir);
                            cpuBoost = targetBoost;
                            gpuBoost = targetBoost;
                            lastBoost = targetBoost;
                        }
                    }
                    else if (!gmodeOn)
                    {
                        int targetBoost = EvaluateAdaptiveBoost(maxTemp);
                        if (Math.Abs(targetBoost - lastBoost) >= 3 || Math.Abs(targetBoost - cpuBoost) >= 5 || Math.Abs(targetBoost - gpuBoost) >= 5)
                        {
                            RunCli(AlienFanCliPath, $"setpower=0 setfans={targetBoost},{targetBoost}", AlienToolsDir);
                            cpuBoost = targetBoost;
                            gpuBoost = targetBoost;
                            lastBoost = targetBoost;
                        }
                    }
                }
                else
                {
                    // G-Mode travado pelo usuário em 100%
                    if (!gmodeOn)
                    {
                        RunCli(AlienFanCliPath, "setgmode=1", AlienToolsDir);
                        gmodeOn = true;
                    }
                }

                return (cpuRpm, gpuRpm, cpuBoost, gpuBoost, cpuTemp, gpuTemp, gmodeOn, powerMode, lastBoost);
            });

            _lastAppliedBoost = data.lastBoost;

            // No Modo Dinâmico, exibe o status visual real sem sobrescrever a preferência do usuário
            if (G15LivePowerMode != null)
            {
                if (_settings.G15.GModeEnabled)
                {
                    G15LivePowerMode.Text = "G-MODE · FIXO";
                    G15LivePowerSub.Text = "100% Força Bruta (~5200 RPM)";
                }
                else
                {
                    G15LivePowerMode.Text = data.gmodeOn ? "DINÂMICO (TURBO 100%)" : "DINÂMICO · ADAPTATIVO";
                    G15LivePowerSub.Text = data.gmodeOn ? "Disparo automático (~5200 RPM)" : "Silencioso / Curva suave";
                }
            }

            if (data.cpuRpm >= 0 || data.cpuTemp > 0)
            {
                string rpmStr = data.cpuRpm >= 0 ? $"{data.cpuRpm} RPM" : "— RPM";
                G15LiveCpuTemp.Text = data.cpuTemp > 0 ? $"{data.cpuTemp} °C · {rpmStr}" : rpmStr;
                G15LiveCpuBoost.Text = data.cpuBoost >= 0 ? $"Boost ativo: {data.cpuBoost}%" : "Boost ativo: —%";
            }
            if (data.gpuRpm >= 0 || data.gpuTemp > 0)
            {
                string rpmStr = data.gpuRpm >= 0 ? $"{data.gpuRpm} RPM" : "— RPM";
                G15LiveGpuTemp.Text = data.gpuTemp > 0 ? $"{data.gpuTemp} °C · {rpmStr}" : rpmStr;
                G15LiveGpuBoost.Text = data.gpuBoost >= 0 ? $"Boost ativo: {data.gpuBoost}%" : "Boost ativo: —%";
            }


            if (GpuAbLiveTemp != null && data.gpuTemp > 0)
            {
                GpuAbLiveTemp.Text = $"{data.gpuTemp} °C";
            }
            if (CpuUxtuLiveTemp != null && data.cpuTemp > 0)
            {
                CpuUxtuLiveTemp.Text = $"{data.cpuTemp} °C";
            }
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
        int b = (int)Math.Round(G15KbBrightSlider.Value);
        if (G15KbBrightValue != null) G15KbBrightValue.Text = $"{b} %";
        if (G15KbBrightBox != null && !G15KbBrightBox.IsKeyboardFocusWithin) G15KbBrightBox.Text = b.ToString();
        _settings.G15.KbBrightness = b;
    }

    private void G15KbBrightBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitG15KbBrightBox();
    }

    private void CommitG15KbBrightBox()
    {
        if (_g15UiInitializing || G15KbBrightBox == null) return;
        string clean = Regex.Replace(G15KbBrightBox.Text ?? "", @"[^\d]", "");
        if (int.TryParse(clean, out int val))
        {
            val = Math.Clamp(val, 0, 100);
            G15KbBrightSlider.Value = val;
            G15KbBrightBox.Text = val.ToString();
            _settings.G15.KbBrightness = val;
            _settings.Save();
        }
        else
        {
            G15KbBrightBox.Text = ((int)Math.Round(G15KbBrightSlider.Value)).ToString();
        }
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
        {
            int b = (int)Math.Round(G15KbBrightSlider.Value);
            G15KbBrightValue.Text = $"{b} %";
            if (G15KbBrightBox != null && !G15KbBrightBox.IsKeyboardFocusWithin)
                G15KbBrightBox.Text = b.ToString();
        }

        string raw = G15KbHexBox.Text.Trim();
        if (!raw.StartsWith('#')) raw = "#" + raw;

        if (TryParseHexColor(raw, out byte r, out byte g, out byte bCol))
        {
            G15KbColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, bCol));
            _settings.G15.KbColorHex = $"#{r:X2}{g:X2}{bCol:X2}";
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
        CommitG15KbBrightBox();
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

        // Desativa as tarefas redundantes do Afterburner, UXTU e AlienFX na inicialização do Windows,
        // já que o Araújo Control Suite assumiu 100% do controle (NVAPI nativo, SMU PawnIO, AlienFan e AlienFX).
        // CleanupLegacyG15Apps(); // Mantém as tarefas originais (Afterburner, UXTU, alienfx-gui) intactas
    }

    public static void RestoreLegacyG15AppsAndDisableSelf()
    {
        foreach (string legacyTask in new[] { "\\MSIAfterburner", "\\UXTU", "\\alienfx-gui" })
        {
            try
            {
                using var en = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{legacyTask}\" /Enable",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                en?.WaitForExit(2000);
            }
            catch { }
        }

        foreach (string legacyTask in new[] { "\\MSIAfterburner", "\\UXTU", "\\alienfx-gui" })
        {
            try
            {
                using var run = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Run /TN \"{legacyTask}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                run?.WaitForExit(2000);
            }
            catch { }
        }

        try
        {
            using var del = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"\\{ScheduledTaskName}\" /F",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            del?.WaitForExit(2000);
        }
        catch { }
    }

    public static void CleanupLegacyG15Apps()
    {
        foreach (string legacyTask in new[] { "\\MSIAfterburner", "\\UXTU", "\\alienfx-gui" })
        {
            try
            {
                using var dis = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{legacyTask}\" /Disable",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                dis?.WaitForExit(2000);
            }
            catch { }
        }
    }

    // ---------------- Bandeja do Sistema (System Tray) ----------------

    private TrayIcon? _trayIcon;
    private bool _realExit;

    private void InitTrayIcon(IntPtr hwnd)
    {
        _trayIcon = new TrayIcon(hwnd)
        {
            QueryState = () => (_settings.G15.GModeEnabled, _settings.G15.ActiveGpuProfile, ChkOverlay.IsChecked == true)
        };
        _trayIcon.OpenRequested += RestoreFromTray;
        _trayIcon.MenuCommandRequested += OnTrayMenuCommand;

        if (_settings.G15.StartMinimized && Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() => SendToTray(showBalloon: false)), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public void SendToTray(bool showBalloon = true)
    {
        ShowInTaskbar = false;
        Hide();
        if (showBalloon)
        {
            _trayIcon?.ShowMinimizedBalloonOnce();
        }
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ExitApplicationCompletely()
    {
        _realExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private async void OnTrayMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case TrayIcon.CMD_OPEN:
                RestoreFromTray();
                break;

            case TrayIcon.CMD_G15:
                RestoreFromTray();
                if (NavAlienFan != null) NavAlienFan.IsChecked = true;
                ShowPage("PageAlienFan");
                TitlePage.Text = "TUNING & HARDWARE · AlienFan (Refrigeração & G-Mode)";
                break;

            case TrayIcon.CMD_FAN_ADAPT:
            case TrayIcon.CMD_FAN_GMODE:
            {
                bool gmode = cmd == TrayIcon.CMD_FAN_GMODE;
                _settings.G15.GModeEnabled = gmode;
                _g15UiInitializing = true;
                RbG15FanAdaptive.IsChecked = !gmode;
                RbG15FanGMode.IsChecked = gmode;
                _g15UiInitializing = false;
                _settings.Save();
                string msg = await Task.Run(() => ApplyG15ThermalModeInternal(gmode));
                G15FanStatus.Text = msg;
                await RefreshG15SensorsAsync();
                break;
            }

            case TrayIcon.CMD_GPU_P1:
            case TrayIcon.CMD_GPU_P2:
            {
                int prof = cmd == TrayIcon.CMD_GPU_P2 ? 2 : 1;
                _settings.G15.ActiveGpuProfile = prof;
                _g15UiInitializing = true;
                RbG15GpuP1.IsChecked = prof == 1;
                RbG15GpuP2.IsChecked = prof == 2;
                _g15UiInitializing = false;
                SyncG15GpuSlidersToProfile(prof);
                _settings.Save();
                string msg = await Task.Run(() => ApplyG15GpuProfileInternal(_settings.G15));
                G15GpuStatus.Text = msg;
                UpdateG15QuickProfileBadge();
                break;
            }

            case TrayIcon.CMD_OVERLAY:
                ChkOverlay.IsChecked = ChkOverlay.IsChecked != true;
                break;

            case TrayIcon.CMD_EXIT:
                ExitApplicationCompletely();
                break;
        }
    }
}
