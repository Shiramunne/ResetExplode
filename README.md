<p align="center">
  <img src="Assets/banner.png" alt="Araújo Control Suite Banner" width="840" />
</p>

# ARAÚJO CONTROL SUITE — v0.16

> **Repositório oficial:** [github.com/Shiramunne/ResetExplode](https://github.com/Shiramunne/ResetExplode)
> Suíte unificada de diagnóstico, telemetria, benchmark, overlay e controle dedicado de hardware (**G15 Control**).
> Baseado no projeto original [ResetExplode](https://github.com/GustavoReset/ResetExplode) (licença [MIT](LICENSE)).

## Destaques · Central G15 Control (Dell G15 5515 Edition)
- **GPU Tuning (NVIDIA RTX 3060 · MSI Afterburner Engine)**:
  - **Perfil 1 (Otimizado · Undervolt)**: trava na curva V/F (`850 mV @ 1860 MHz` + `+500 MHz` VRAM) para máxima eficiência térmica sem perda de FPS.
  - **Perfil 2 (Compatibilidade)**: curva elevada (`1000 mV` + `+950 MHz` Core) com sliders estilo Afterburner mostrando valores de `mV` e `MHz` em tempo real.
- **CPU Tuning (AMD Ryzen 7 5800H · SMU Direct)**:
  - Aplicação direta nos registradores SMU via PawnIO (`tctl-temp`, `cHTC-temp`, `stapm-limit`, `slow-limit`, `fast-limit`) e sincronização automática com o preset do UXTU (`88 °C · 35W/35W/42W`).
- **Refrigeração & G-Mode (Dell G15 AlienFan)**:
  - Monitoramento rápido de sensores ACPI/EC (CPU, GPU, RPM das duas ventoinhas e % de boost).
  - Alternância instantânea entre **Modo Adaptativo Inteligente (Curva 8 Pontos)** e **G-Mode (Força Bruta 100%)**.
- **Iluminação do Teclado (AlienFX 4 Zonas)**:
  - Seletor RGB em formato HEX (`#RRGGBB`) com amostra ao vivo, cores rápidas e controle de brilho (`0–100%`) direto no controlador USB HID (`VID_187C&PID_0550`).
- **Automação & Inicialização com o Windows**:
  - Tarefa agendada com privilégio elevado (`\AraujoControlSuite`), opção de iniciar minimizado e reaplicação automática de todo o perfil otimizado ao ligar o notebook.

---

## Funcionalidades da Suíte (.NET 8 WPF)
- **Informações do sistema** (categorias + detalhes): SO, placa-mãe/BIOS, CPU, RAM (por módulo), GPU, discos — via WMI.
  Em Armazenamento, cartões por unidade (C:, pendrive, HD externo, rede) com anel de cheio/vazio, tipo e disco físico (`DriveMap.cs`).
- **Avisos de dispositivo** (`ToastWindow.xaml`): ao conectar/remover pendrive, HD externo ou qualquer dispositivo USB (ex.: Arduino),
  aparece um aviso no canto da tela e a lista de unidades se atualiza (via `WM_DEVICECHANGE`, sem ficar consultando).
- **Sensores em tempo real**: temperatura, clock, carga, voltagem, ventoinhas, consumo etc. — via LibreHardwareMonitorLib. Atualiza a cada 1 s, com filtro por tipo.
- **Placa de vídeo** (estilo GPU-Z): chip, arquitetura, processo, die, transistores, shaders/SMs/Tensor/RT, ROPs/TMUs, fillrates, TFLOPS,
  memória (tamanho, tipo, barramento, clock, banda, Resizable BAR), clocks, PCIe (máximo e em uso), limites de energia,
  VBIOS, driver, assinatura WHQL, CUDA, DirectX (feature level), APIs e recursos da arquitetura.
- **Stress test**: carga de CPU em todos os threads (FPU + inteiros) e/ou RAM (grava e verifica padrões em 40–80% da memória livre).
- **Stress de GPU** (inspirado no FurMark, `GpuBurn.cs`): torus com pelos renderizado por *shells* em Direct3D 11 (Vortice).
- **Jogos**: FPS esperado em 32 jogos populares para a sua GPU + CPU, por resolução (1080p/1440p/4K), gráficos e upscaling (DLSS/FSR).
- **Detector de gargalo**: equilíbrio teórico GPU × CPU; leitura ao vivo e análise de sessão (1/3/5 min).
- **Overlay de jogos** (estilo RTSS/Afterburner, `OverlayWindow.xaml`): FPS, frametime, 1% low, gráfico de frametime, GPU/CPU/RAM/VRAM. Atalho global **Ctrl+Shift+O**.
- **Benchmarks integrados** (`BenchRenderer.cs`, `GpuGameBench.cs`, `GpuRtBench.cs`, `GameSimBench.cs`, `MemoryBench.cs`, `DiskBench.cs`).
- **Otimização do Windows** (`Optimizer.cs`, `WindowsTweaks.cs`): limpeza de memória em espera e ajustes seguros para jogos.
- **Portas COM e Drivers Seriais** (`UsbCleaner.cs`, `MakerDrivers.cs`): diagnóstico e reparo para Arduino, ESP32, CH340, CP210x e FTDI.
- **Saúde dos discos (S.M.A.R.T.)** (`DiskHealth.cs`) e **Relatório exportável** (`ReportBuilder.cs`).

## Como compilar e publicar
```powershell
cd D:\tools\ResetExplode
dotnet run
```
Para gerar o executável único (`..\ResetExplode-dist\AraujoControlSuite.exe`): dê dois cliques em `publicar.bat`.

## Licença
MIT — veja o arquivo [LICENSE](LICENSE).
