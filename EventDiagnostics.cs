using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ResetExplode;

/// <summary>Representa um evento do Windows traduzido e humanizado em português claro.</summary>
public sealed class DiagnosticEvent
{
    public DateTime Timestamp { get; init; }
    public string TimeFormatted => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");
    public string RelativeTime { get; init; } = "";
    public int EventId { get; init; }
    public string ProviderName { get; init; } = "";
    public string Channel { get; init; } = "";
    public byte? Level { get; init; }
    public string SeverityText { get; init; } = "Informativo";
    public string SeverityColor { get; init; } = "#8C8C8C";
    public string Category { get; init; } = "Geral";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Recommendation { get; init; }
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);
    public string RawMessage { get; init; } = "";
    public string SourceBadge => $"{ProviderName} (ID {EventId})";
}

/// <summary>Registro estruturado de Tela Azul (BSOD), queda de energia ou travamento de aplicativo.</summary>
public sealed class CrashReport
{
    public DateTime Timestamp { get; init; }
    public string TimeFormatted => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");
    public string RelativeTime { get; init; } = "";
    public string Type { get; init; } = "Tela Azul (BSOD)";
    public string Title { get; init; } = "";
    public string? StopCodeHex { get; init; }
    public string? StopCodeName { get; init; }
    public string CulpritCategory { get; init; } = "Driver";
    public string Explanation { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string? FaultingApp { get; init; }
    public string? FaultingModule { get; init; }
    public string? DumpPath { get; init; }
    public string SeverityBadge { get; init; } = "CRÍTICO";
    public string SeverityColor { get; init; } = "#FF453A";
    public string RawDetails { get; init; } = "";
    public bool HasDump => !string.IsNullOrEmpty(DumpPath) && File.Exists(DumpPath);
    public bool HasFaultingDetails => !string.IsNullOrEmpty(FaultingApp) || !string.IsNullOrEmpty(FaultingModule);
}

/// <summary>Arquivo de minidump (.dmp) encontrado em C:\Windows\Minidump.</summary>
public sealed record MinidumpFile(string Name, string Path, DateTime Created, long SizeBytes)
{
    public string CreatedFormatted => Created.ToString("dd/MM/yyyy HH:mm:ss");
    public string SizeFormatted => SizeBytes >= 1024 * 1024
        ? $"{(SizeBytes / (1024.0 * 1024.0)):F1} MB"
        : $"{(SizeBytes / 1024.0):F0} KB";
}

/// <summary>
/// Motor central de diagnóstico herdado e expandido do Pulse:
/// Humanizador de eventos do Windows, catálogo de 50+ BSOD Stop Codes e análise de quedas de aplicativos.
/// </summary>
public static class EventDiagnostics
{
    // =========================================================================
    // Catálogo de Códigos de Tela Azul (Stop Codes / BugCheck)
    // =========================================================================

    public sealed record BugcheckEntry(
        uint Code,
        string Name,
        string Culprit,
        string Explanation,
        string Recommendation);

    private static readonly Dictionary<uint, BugcheckEntry> BugcheckCatalog = new()
    {
        [0x0000000A] = new(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL", "Driver",
            "Um driver de dispositivo tentou acessar um endereço de memória do kernel sem nível de permissão adequado.",
            "Atualize ou reverta drivers instalados recentemente (placa de vídeo, rede, áudio ou chipset)."),

        [0x00000019] = new(0x00000019, "BAD_POOL_HEADER", "Memória",
            "O cabeçalho do pool de memória foi corrompido, geralmente por conflito entre drivers ou falha de RAM.",
            "Desinstale softwares antivírus/anticheat conflitantes e teste a memória RAM com o Diagnóstico do Windows."),

        [0x0000001A] = new(0x0000001A, "MEMORY_MANAGEMENT", "Memória RAM",
            "Erro grave no subsistema de gerenciamento de memória do Windows. Quase sempre indica instabilidade nos pentes de RAM.",
            "Desative temporariamente overclock de memória (perfil XMP ou EXPO na BIOS) e rode o Diagnóstico de Memória do Windows."),

        [0x0000001E] = new(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED", "Driver",
            "Um programa em modo kernel gerou uma exceção que o manipulador de erros do Windows não conseguiu capturar.",
            "Identifique o arquivo de driver (.sys) mencionado no dump e atualize o componente correspondente."),

        [0x00000024] = new(0x00000024, "NTFS_FILE_SYSTEM", "Armazenamento",
            "Ocorreu um erro no driver do sistema de arquivos NTFS durante leitura ou gravação.",
            "Abra o Prompt de Comando como Administrador e execute chkdsk /f /r para reparar setores corrompidos."),

        [0x0000003B] = new(0x0000003B, "SYSTEM_SERVICE_EXCEPTION", "Driver / Sistema",
            "Uma rotina de serviço do sistema executou código que causou uma violação de acesso.",
            "Muito comum após atualização de drivers de GPU ou periféricos. Reverta o driver mais recente."),

        [0x0000004E] = new(0x0000004E, "PFN_LIST_CORRUPT", "Memória RAM",
            "A lista de números de quadro de página de memória física (PFN) foi corrompida.",
            "Geralmente causado por pente de memória defeituoso ou instável. Teste os pentes individualmente."),

        [0x00000050] = new(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA", "Memória / Driver",
            "O sistema solicitou memória de uma área inválida que não pode ser paginada para o disco.",
            "Causado por driver defeituoso ou RAM física com erro. Desative XMP/EXPO e atualize drivers."),

        [0x00000077] = new(0x00000077, "KERNEL_STACK_INPAGE_ERROR", "Armazenamento / SSD",
            "O Windows tentou ler uma página da pilha do kernel a partir do disco, mas a leitura falhou.",
            "Verifique os cabos de dados/alimentação do disco, verifique a saúde do SSD e rode chkdsk."),

        [0x0000007A] = new(0x0000007A, "KERNEL_DATA_INPAGE_ERROR", "Armazenamento / SSD",
            "Uma página solicitada de dados do kernel não pôde ser lida do dispositivo de armazenamento.",
            "Indício forte de falha iminente ou desconexão momentânea do SSD/HD. Faça backup dos arquivos críticos."),

        [0x0000007B] = new(0x0000007B, "INACCESSIBLE_BOOT_DEVICE", "Armazenamento / Boot",
            "O Windows perdeu o acesso à partição de inicialização do sistema durante a carga do SO.",
            "Verifique as configurações de modo SATA/NVMe (AHCI/RAID) na BIOS e a ordem de boot dos discos."),

        [0x0000007E] = new(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Driver",
            "Uma thread do sistema operacional gerou uma exceção que não foi tratada.",
            "Certifique-se de que há espaço livre suficiente na unidade C: e atualize a BIOS da placa-mãe."),

        [0x0000007F] = new(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP", "Hardware / CPU",
            "Uma armadilha ou falha de hardware foi gerada pela CPU e o kernel não pôde se recuperar.",
            "Verifique se o processador está superaquecendo, limpe o cooler e desfaça qualquer overclock de CPU."),

        [0x00000080] = new(0x00000080, "NMI_HARDWARE_FAILURE", "Hardware",
            "Uma interrupção não mascarável (NMI) foi acionada por um erro físico grave de hardware.",
            "Verifique se a fonte de alimentação está fornecendo tensões estáveis e reconecte placas PCIe."),

        [0x0000008E] = new(0x0000008E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", "Driver",
            "Exceção em modo kernel não tratada. Comum em drivers legados incompatíveis.",
            "Atualize todos os drivers do computador e remova softwares de virtualização antigos."),

        [0x0000009C] = new(0x0000009C, "MACHINE_CHECK_EXCEPTION", "Hardware / Processador",
            "A arquitetura de verificação de máquina da CPU detectou um erro interno irreparável de processamento.",
            "Remova qualquer undervolt ou overclock da CPU, verifique temperaturas e atualize a BIOS da placa-mãe."),

        [0x0000009F] = new(0x0000009F, "DRIVER_POWER_STATE_FAILURE", "Driver / Energia",
            "Um driver não respondeu a uma mudança de estado de energia (como suspensão, hibernação ou despertar).",
            "Atualize drivers de chipset, Wi-Fi e placa de vídeo. Desative a Inicialização Rápida nas opções de energia."),

        [0x000000A0] = new(0x000000A0, "INTERNAL_POWER_ERROR", "Driver / Hibernação",
            "O gerenciador de energia do Windows encontrou um erro fatal ao alternar estados de energia.",
            "Desative e reative o arquivo de hibernação (powercfg -h off e depois powercfg -h on no terminal)."),

        [0x000000BE] = new(0x000000BE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Driver",
            "Um driver tentou gravar em uma região de memória marcada como somente leitura.",
            "Atualize ou remova o driver ofensivo identificado no relatório de despejo de memória."),

        [0x000000C2] = new(0x000000C2, "BAD_POOL_CALLER", "Driver",
            "Uma chamada de pool de memória inválida foi feita por uma rotina de driver.",
            "Reverta atualizações recentes de software de monitoramento de sistema ou drivers de terceiros."),

        [0x000000C4] = new(0x000000C4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver",
            "O Verificador de Drivers do Windows detectou uma violação das regras de estabilidade do kernel.",
            "Desative o Driver Verifier com 'verifier /reset' no terminal se o sistema estiver instável."),

        [0x000000C5] = new(0x000000C5, "DRIVER_CORRUPTED_EXPOOL", "Driver",
            "Um driver corrompeu a estrutura do pool executivo do Windows ao acessar memória em nível inválido.",
            "Verifique qual driver causou a falha no dump de memória e instale a versão fornecida pelo fabricante."),

        [0x000000D1] = new(0x000000D1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Driver de Dispositivo",
            "Um driver de dispositivo acessou um endereço de memória paginável em um nível IRQL muito elevado.",
            "Muito frequente em drivers de placa de rede Wi-Fi, Ethernet ou placas de áudio. Atualize o driver."),

        [0x000000D5] = new(0x000000D5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "Driver",
            "Um driver tentou referenciar memória que já havia sido liberada anteriormente.",
            "Atualize os drivers de dispositivos conectados via USB ou controladoras de disco."),

        [0x000000E2] = new(0x000000E2, "MANUALLY_INITIATED_CRASH", "Manual",
            "O crash foi acionado propositalmente pelo usuário por atalho de depuração ou software.",
            "Nenhuma ação necessária caso tenha sido um teste intencional."),

        [0x000000EA] = new(0x000000EA, "THREAD_STUCK_IN_DEVICE_DRIVER", "Placa de Vídeo / Driver",
            "Uma thread ficou travada em loop infinito dentro do driver do adaptador de vídeo.",
            "Atualize o driver da placa de vídeo de forma limpa ou reverta para uma versão anterior estável."),

        [0x000000ED] = new(0x000000ED, "UNMOUNTABLE_BOOT_VOLUME", "Armazenamento",
            "O Windows não conseguiu montar o volume de inicialização do sistema operacional.",
            "Inicie com mídia de instalação do Windows e rode 'chkdsk /r c:' no console de recuperação."),

        [0x000000EF] = new(0x000000EF, "CRITICAL_PROCESS_DIED", "Processo do Sistema",
            "Um processo vital para o funcionamento do Windows (como csrss.exe ou wininit.exe) encerrou repentinamente.",
            "Rode 'sfc /scannow' e 'DISM /Online /Cleanup-Image /RestoreHealth' no Prompt de Comando como Admin."),

        [0x000000F4] = new(0x000000F4, "CRITICAL_OBJECT_TERMINATION", "Processo do Sistema",
            "Um processo crítico de subsistema encerrou sua execução de modo inesperado.",
            "Verifique infecções por malware e analise se o SSD principal não está sofrendo quedas de comunicação."),

        [0x000000FC] = new(0x000000FC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Driver",
            "Um driver tentou executar instruções a partir de uma área de memória protegida por DEP (Data Execution Prevention).",
            "Atualize os drivers de sistema e softwares de segurança ou antivírus instalados."),

        [0x000000FE] = new(0x000000FE, "BUGCODE_USB_DRIVER", "Driver USB",
            "Ocorreu um erro fatal dentro da pilha de drivers dos controladores USB.",
            "Desconecte dispositivos USB externos e atualize os drivers do chipset da placa-mãe."),

        [0x00000101] = new(0x00000101, "CLOCK_WATCHDOG_TIMEOUT", "Hardware / Processador",
            "Um dos núcleos da CPU não respondeu à interrupção de clock dentro do tempo limite esperado.",
            "Forte sintoma de instabilidade de overclock de CPU, voltagem de núcleo (Vcore) insuficiente ou superaquecimento."),

        [0x00000109] = new(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION", "Memória / Kernel",
            "O Windows detectou corrupção em uma estrutura crítica de dados do kernel.",
            "Teste os módulos de memória RAM. Pode ser causado também por drivers de anticheat de terceiros."),

        [0x0000010E] = new(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "Placa de Vídeo / VRAM",
            "O gerenciador de memória de vídeo da GPU encontrou uma inconsistência interna irrecuperável.",
            "Reduza overclock da VRAM da placa de vídeo e instale o driver de vídeo oficial mais recente."),

        [0x00000113] = new(0x00000113, "VIDEO_DXGKRNL_FATAL_ERROR", "Placa de Vídeo / DirectX",
            "O subsistema de kernel do DirectX encontrou uma violação fatal.",
            "Atualize o driver da GPU e verifique a integridade das bibliotecas do DirectX."),

        [0x00000116] = new(0x00000116, "VIDEO_TDR_FAILURE", "Placa de Vídeo (GPU)",
            "O driver da placa de vídeo parou de responder e não conseguiu se recuperar a tempo (Timeout Detection and Recovery).",
            "Reinstale o driver da placa de vídeo com instalação limpa (usando DDU se persistir) e verifique temperaturas."),

        [0x00000117] = new(0x00000117, "VIDEO_TDR_TIMEOUT_DETECTED", "Placa de Vídeo (GPU)",
            "A placa de vídeo demorou tempo excessivo para completar uma operação gráfica.",
            "Diminua configurações gráficas em jogos pesados e verifique se o conector de energia PCIe da GPU está bem encaixado."),

        [0x00000119] = new(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "Placa de Vídeo (GPU)",
            "O agendador de GPU encontrou uma condição inconsistente na fila de comandos gráficos.",
            "Atualize o driver da placa de vídeo e desative a aceleração de hardware em navegadores se ocorrer fora de jogos."),

        [0x00000124] = new(0x00000124, "WHEA_UNCORRECTABLE_ERROR", "Hardware (WHEA)",
            "A Arquitetura de Erro de Hardware do Windows detectou um erro fatal de hardware irrecuperável.",
            "Geralmente causado por overclock instável de CPU/RAM, aquecimento extremo, oscilação de fonte ou SSD NVMe com falha física."),

        [0x0000012B] = new(0x0000012B, "FAULTY_HARDWARE_CORRUPTED_PAGE", "Memória RAM",
            "O Windows encontrou uma página de memória física com conteúdo corrompido de forma recorrente.",
            "Execute o Diagnóstico de Memória do Windows e teste os módulos de memória individualmente para isolar o pente com defeito."),

        [0x00000133] = new(0x00000133, "DPC_WATCHDOG_VIOLATION", "Timeout / Driver",
            "Uma rotina de chamada de procedimento diferido (DPC) executou por tempo excessivo, bloqueando o sistema operacional.",
            "Atualize o firmware do SSD, os drivers de armazenamento (SATA/NVMe) e os drivers de rede e chipset."),

        [0x00000139] = new(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE", "Driver",
            "Uma verificação de integridade de segurança do kernel falhou devido a corrupção de listas de dados.",
            "Atualize drivers recentes e execute 'sfc /scannow' no Prompt de Comando como Administrador."),

        [0x0000013A] = new(0x0000013A, "KERNEL_MODE_HEAP_CORRUPTION", "Driver",
            "O heap de memória em modo kernel foi corrompido por uma operação de driver defeituosa.",
            "Desinstale softwares recentes que instalam drivers de filtro (como anticheats, VPNs ou softwares de teclado/mouse)."),

        [0x00000144] = new(0x00000144, "BUGCODE_USB3_DRIVER", "Driver USB 3.0",
            "Ocorreu um erro irrecuperável no driver de controle de barramento USB 3.0.",
            "Atualize os drivers de controladora USB e os drivers do chipset da placa-mãe."),

        [0x00000154] = new(0x00000154, "UNEXPECTED_STORE_EXCEPTION", "Armazenamento / SSD",
            "O componente de armazenamento de memória compactada do Windows falhou ao tentar ler do disco.",
            "Sintoma muito frequente de SSD/NVMe superaquecendo ou desconectando momentaneamente. Verifique a saúde do disco."),

        [0x000001C8] = new(0x000001C8, "MANUALLY_INITIATED_POWER_BUTTON_HOLD", "Manual (Botão Power)",
            "O desligamento/reboot forçado foi acionado pelo usuário segurando o botão Power do gabinete.",
            "Comportamento normal quando o botão de energia é mantido pressionado propositalmente."),

        [0x1000007E] = new(0x1000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "Driver",
            "Exceção de thread de sistema não tratada durante execução em modo kernel.",
            "Verifique os drivers atualizados recentemente e certifique-se de que há espaço em disco."),

        [0x1000008E] = new(0x1000008E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", "Driver",
            "Exceção de modo kernel não tratada.",
            "Atualize os drivers dos componentes instalados no PC."),

        [0xC000021A] = new(0xC000021A, "WINLOGON_FATAL_ERROR", "Processo do Sistema",
            "O subsistema de logon do usuário (winlogon.exe) ou csrss.exe parou de funcionar de forma catastrófica.",
            "Inicie o Windows no Modo de Segurança e reverta alterações de sistema recentes ou restaure o sistema."),

        [0xDEADDEAD] = new(0xDEADDEAD, "MANUALLY_INITIATED_CRASH1", "Manual",
            "Crash iniciado propositalmente por depurador.",
            "Comportamento intencional acionado por ferramenta de teste.")
    };

    public static BugcheckEntry FindBugcheck(uint code)
    {
        if (BugcheckCatalog.TryGetValue(code, out var entry)) return entry;
        return new BugcheckEntry(
            code,
            $"STOP_0x{code:X8}",
            "Não Identificado",
            "O Windows registrou uma verificação de erro crítica (tela azul) com código não catalogado.",
            "Abra o relatório do minidump correspondente para identificar o módulo ou driver causador.");
    }

    // =========================================================================
    // Catálogo de Exceções de Travamento de Aplicativos (Event ID 1000)
    // =========================================================================

    private static readonly Dictionary<uint, string> ExceptionMeanings = new()
    {
        [0xC0000005] = "Violação de Acesso à Memória (o aplicativo tentou ler ou gravar em um endereço inválido)",
        [0xC00000FD] = "Estouro de Pilha (Stack Overflow - o aplicativo esgotou o limite da pilha de execução)",
        [0xC0000374] = "Corrupção de Heap (a área de memória alocada dinamicamente pelo programa foi danificada)",
        [0x80000003] = "Ponto de Parada Atingido (Breakpoint de depuração acionado ou falha de asserção interna)",
        [0xC0000409] = "Estouro de Buffer com Disparo de Segurança (Fail Fast - o Windows fechou o aplicativo por proteção)",
        [0xC000041D] = "Exceção não tratada durante retorno de callback do usuário",
        [0xC0000008] = "Identificador de objeto (Handle) inválido referenciado pelo aplicativo",
        [0xC000001D] = "Instrução de CPU ilegal ou não suportada pelo processador atual",
        [0xC0000025] = "Exceção não continuável",
        [0xC0000094] = "Divisão inteira por zero",
        [0xC0000096] = "Instrução privilegiada executada sem permissão",
        [0xE0434352] = "Exceção fatal não tratada na plataforma .NET (CLR)",
        [0xE06D7363] = "Exceção de software do compilador Microsoft Visual C++"
    };

    public static string ExplainExceptionCode(uint code)
    {
        if (ExceptionMeanings.TryGetValue(code, out var meaning))
            return $"{meaning} (0x{code:X8})";
        return $"Erro desconhecido (0x{code:X8})";
    }

    // =========================================================================
    // Humanizador de Eventos do Windows (Linha do Tempo)
    // =========================================================================

    private sealed record HumanRule(
        int EventId,
        string ProviderHint,
        string Title,
        string Summary,
        string Recommendation,
        string Category);

    private static readonly HumanRule[] Rules =
    {
        new(10016, "DistributedCOM", "Aviso de Permissão DCOM",
            "Um aplicativo tentou acessar um componente DCOM sem permissão explícita configurada.",
            "Comportamento comum e inofensivo do Windows. Nenhuma ação é necessária.",
            "Sistema"),

        new(7036, "Service Control Manager", "Estado de Serviço Alterado",
            "Um serviço em segundo plano do Windows mudou seu estado de execução (iniciou ou parou).",
            "",
            "Serviços"),

        new(7040, "Service Control Manager", "Inicialização de Serviço Alterada",
            "O Windows alterou o modo de inicialização de um serviço em segundo plano.",
            "",
            "Serviços"),

        new(7023, "Service Control Manager", "Falha na Execução de Serviço",
            "Um serviço do Windows encontrou um erro interno e não conseguiu continuar operando.",
            "Verifique o nome do serviço afetado e considere reinstalar o programa associado.",
            "Serviços"),

        new(7031, "Service Control Manager", "Serviço Travou Inesperadamente",
            "Um serviço encerrou repentinamente e o Windows agendou uma tentativa de recuperação automática.",
            "Falhas repetidas para o mesmo serviço sugerem arquivo corrompido ou conflito de versão.",
            "Serviços"),

        new(7034, "Service Control Manager", "Serviço Finalizado Inesperadamente",
            "Um serviço do Windows foi encerrado de modo anômalo.",
            "Verifique se há erros simultâneos de outros componentes no mesmo minuto.",
            "Serviços"),

        new(7045, "Service Control Manager", "Novo Serviço ou Driver Instalado",
            "O Windows registrou a instalação de um novo serviço ou driver no sistema operacional.",
            "",
            "Drivers"),

        new(12, "Kernel-General", "Windows Inicializado com Sucesso",
            "O sistema operacional Windows completou o processo de inicialização com êxito.",
            "",
            "Inicialização"),

        new(13, "Kernel-General", "Windows Desligado com Sucesso",
            "O sistema operacional foi desligado de maneira ordenada e limpa.",
            "",
            "Inicialização"),

        new(6005, "EventLog", "Serviço de Logs Iniciado",
            "O serviço de registro de eventos do Windows começou a gravar atividades.",
            "",
            "Inicialização"),

        new(6006, "EventLog", "Serviço de Logs Encerrado",
            "O serviço de registro de eventos do Windows parou suas atividades.",
            "",
            "Inicialização"),

        new(1074, "User32", "Desligamento ou Reinício Solicitado",
            "Um usuário ou aplicativo solicitou o encerramento ou reinicialização do computador.",
            "",
            "Inicialização"),

        new(41, "Kernel-Power", "Reinício Inesperado (Queda de Energia ou Crash)",
            "O computador foi reinicializado sem ter sido desligado corretamente primeiro.",
            "Verifique se faltou luz, se o botão Power foi segurado ou se ocorreu uma tela azul.",
            "Energia"),

        new(6008, "EventLog", "Desligamento Anterior Foi Inesperado",
            "O Windows registrou que o desligamento da sessão anterior ocorreu de forma abrupta.",
            "Correlacione com o evento Kernel-Power 41 para diagnosticar a causa.",
            "Energia"),

        new(42, "Kernel-Power", "Windows Entrou em Suspensão",
            "O computador entrou no modo de suspensão/dormir para economizar energia.",
            "",
            "Energia"),

        new(107, "Kernel-Power", "Windows Retomou da Suspensão",
            "O computador acordou do estado de suspensão e retornou à operação normal.",
            "",
            "Energia"),

        new(1, "Power-Troubleshooter", "Windows Despertou do Modo de Espera",
            "O computador despertou do modo de espera ou da hibernação.",
            "",
            "Energia"),

        new(1001, "WER-SystemErrorReporting", "Registro de Falha do Sistema (BugCheck)",
            "O Windows registrou uma falha crítica de parada do sistema e gerou um relatório de erro.",
            "Verifique o código de parada (Stop Code) na aba de Telas Azuis para diagnosticar a causa.",
            "Falhas"),

        new(1001, "BugCheck", "Tela Azul Registrada (BSOD)",
            "O Windows sofreu uma verificação de erro crítica (tela azul).",
            "Consulte o código e o arquivo minidump gravado para saber qual driver ou peça falhou.",
            "Falhas"),

        new(1000, "Application Error", "Aplicativo Travou ou Fechou Repentinamente",
            "Um programa encerrou de maneira inesperada devido a uma falha de execução interna.",
            "Verifique o módulo com falha e o código de exceção na aba de Telas Azuis e Falhas.",
            "Falhas"),

        new(1002, "Application Hang", "Aplicativo Parou de Responder (Congelou)",
            "O Windows detectou que um programa parou de responder a eventos de interface.",
            "Se o congelamento for frequente, procure atualizações do aplicativo.",
            "Falhas"),

        new(4101, "Display", "Driver de Vídeo Travou e se Recuperou (TDR)",
            "O driver da placa de vídeo parou de responder, mas o Windows conseguiu recuperá-lo sem desligar.",
            "Reinstale o driver da placa de vídeo de forma limpa e monitore as temperaturas da GPU.",
            "Vídeo"),

        new(19, "WindowsUpdateClient", "Atualização do Windows Instalada",
            "O Windows Update instalou um pacote de atualização com sucesso.",
            "",
            "Atualização"),

        new(20, "WindowsUpdateClient", "Falha ao Instalar Atualização do Windows",
            "O Windows Update tentou instalar um pacote de atualização, mas ocorreu um erro.",
            "Tente executar o solucionador de problemas do Windows Update ou limpar a pasta SoftwareDistribution.",
            "Atualização"),

        new(43, "WindowsUpdateClient", "Instalação de Atualização Iniciada",
            "O Windows iniciou o processo de aplicação de uma atualização do sistema.",
            "",
            "Atualização"),

        new(44, "WindowsUpdateClient", "Download de Atualização Iniciado",
            "O Windows Update começou a baixar um pacote de atualização.",
            "",
            "Atualização"),

        new(400, "Kernel-PnP", "Dispositivo Plug and Play Configurado",
            "O Windows concluiu a configuração de um dispositivo de hardware conectado.",
            "",
            "Dispositivos"),

        new(410, "Kernel-PnP", "Dispositivo Plug and Play Inicializado",
            "O Windows iniciou com sucesso um dispositivo de hardware.",
            "",
            "Dispositivos"),

        new(411, "Kernel-PnP", "Dispositivo Falhou ao Iniciar",
            "Um dispositivo de hardware teve problemas para inicializar no sistema.",
            "Verifique o Gerenciador de Dispositivos para reinstalar o driver do periférico.",
            "Dispositivos"),

        new(7, "disk", "Setor Danificado no Disco (Bad Block)",
            "O Windows detectou um bloco defeituoso em um dispositivo de armazenamento.",
            "Faça backup imediato dos seus arquivos e execute um teste de integridade SMART no disco.",
            "Armazenamento"),

        new(51, "disk", "Erro de E/S no Disco",
            "Ocorreu um erro durante uma operação de leitura ou gravação em uma unidade de disco.",
            "Verifique se o cabo do disco está firme e cheque a integridade da unidade.",
            "Armazenamento"),

        new(4624, "Security-Auditing", "Sessão de Usuário Conectada",
            "Uma conta de usuário fez login com sucesso no sistema operacional.",
            "",
            "Segurança"),

        new(4634, "Security-Auditing", "Sessão de Usuário Desconectada",
            "Uma conta de usuário encerrou a sessão no sistema operacional.",
            "",
            "Segurança")
    };

    public static DiagnosticEvent Humanize(EventRecord record)
    {
        int id = record.Id;
        string provider = record.ProviderName ?? "";
        byte? level = record.Level;

        HumanRule? match = Rules.FirstOrDefault(r =>
            r.EventId == id &&
            (string.IsNullOrEmpty(r.ProviderHint) ||
             provider.IndexOf(r.ProviderHint, StringComparison.OrdinalIgnoreCase) >= 0));

        string rawMessage = "";
        try { rawMessage = record.FormatDescription() ?? ""; } catch { }

        var (severityText, severityColor) = level switch
        {
            1 => ("Crítico", "#FF453A"),
            2 => ("Erro", "#FF6B6B"),
            3 => ("Aviso", "#FFB84D"),
            _ => ("Informativo", "#8C8C8C")
        };

        DateTime time = record.TimeCreated ?? DateTime.Now;
        string relative = FormatRelativeTime(time);

        if (match != null)
        {
            return new DiagnosticEvent
            {
                Timestamp = time,
                RelativeTime = relative,
                EventId = id,
                ProviderName = provider,
                Channel = record.LogName ?? "Sistema",
                Level = level,
                SeverityText = severityText,
                SeverityColor = severityColor,
                Category = match.Category,
                Title = match.Title,
                Summary = match.Summary,
                Recommendation = string.IsNullOrWhiteSpace(match.Recommendation) ? null : match.Recommendation,
                RawMessage = rawMessage
            };
        }

        // Fallback inteligente caso não esteja no catálogo estático
        string category = CategorizeProvider(provider);
        string cleanSummary = string.IsNullOrWhiteSpace(rawMessage)
            ? $"Evento registrado pelo provedor {provider}."
            : FirstSentence(rawMessage);

        return new DiagnosticEvent
        {
            Timestamp = time,
            RelativeTime = relative,
            EventId = id,
            ProviderName = provider,
            Channel = record.LogName ?? "Sistema",
            Level = level,
            SeverityText = severityText,
            SeverityColor = severityColor,
            Category = category,
            Title = $"{provider} (ID {id})",
            Summary = cleanSummary,
            Recommendation = null,
            RawMessage = rawMessage
        };
    }

    private static string CategorizeProvider(string provider)
    {
        if (provider.Contains("Kernel", StringComparison.OrdinalIgnoreCase)) return "Sistema";
        if (provider.Contains("Disk", StringComparison.OrdinalIgnoreCase) || provider.Contains("Storage", StringComparison.OrdinalIgnoreCase)) return "Armazenamento";
        if (provider.Contains("Display", StringComparison.OrdinalIgnoreCase) || provider.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) return "Vídeo";
        if (provider.Contains("Power", StringComparison.OrdinalIgnoreCase)) return "Energia";
        if (provider.Contains("Service", StringComparison.OrdinalIgnoreCase)) return "Serviços";
        if (provider.Contains("Net", StringComparison.OrdinalIgnoreCase) || provider.Contains("Wifi", StringComparison.OrdinalIgnoreCase)) return "Rede";
        if (provider.Contains("Security", StringComparison.OrdinalIgnoreCase) || provider.Contains("Audit", StringComparison.OrdinalIgnoreCase)) return "Segurança";
        return "Geral";
    }

    private static string FirstSentence(string text)
    {
        int dot = text.IndexOf('.');
        if (dot > 0 && dot < 160) return text.Substring(0, dot + 1).Trim();
        if (text.Length > 160) return text.Substring(0, 157).Trim() + "...";
        return text.Trim();
    }

    public static string FormatRelativeTime(DateTime time)
    {
        var diff = DateTime.Now - time;
        if (diff.TotalSeconds < 60) return "agora há pouco";
        if (diff.TotalMinutes < 60) return $"há {(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"há {(int)diff.TotalHours} h";
        if (diff.TotalDays < 7) return $"há {(int)diff.TotalDays} dia(s)";
        return time.ToString("dd/MM/yyyy");
    }

    // =========================================================================
    // Métodos Públicos Assíncronos de Coleta
    // =========================================================================

    /// <summary>Lê os eventos recentes do sistema e de aplicativos de forma assíncrona.</summary>
    public static async Task<List<DiagnosticEvent>> GetRecentEventsAsync(int maxEvents = 150)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiagnosticEvent>();

            // Query rápida apenas para Críticos (1), Erros (2) e Avisos (3) + Informações relevantes
            string queryXml = "*[System[(Level=1 or Level=2 or Level=3 or EventID=12 or EventID=13 or EventID=1074 or EventID=6005 or EventID=6006)]]";

            try
            {
                var sysQuery = new EventLogQuery("System", PathType.LogName, queryXml) { ReverseDirection = true };
                using var sysReader = new EventLogReader(sysQuery);
                for (int i = 0; i < maxEvents / 2; i++)
                {
                    var record = sysReader.ReadEvent();
                    if (record == null) break;
                    using (record)
                    {
                        list.Add(Humanize(record));
                    }
                }
            }
            catch { }

            try
            {
                var appQuery = new EventLogQuery("Application", PathType.LogName, queryXml) { ReverseDirection = true };
                using var appReader = new EventLogReader(appQuery);
                for (int i = 0; i < maxEvents / 2; i++)
                {
                    var record = appReader.ReadEvent();
                    if (record == null) break;
                    using (record)
                    {
                        list.Add(Humanize(record));
                    }
                }
            }
            catch { }

            return list.OrderByDescending(e => e.Timestamp).ToList();
        });
    }

    /// <summary>Coleta e analisa o histórico de Telas Azuis (BSOD), reinícios forçados e quedas de aplicativos.</summary>
    public static async Task<List<CrashReport>> GetCrashReportsAsync(int maxItems = 40)
    {
        return await Task.Run(() =>
        {
            var reports = new List<CrashReport>();

            // 1. Telas Azuis e Reinícios Abruptos (System: ID 41 Kernel-Power e ID 1001 WER)
            try
            {
                var sysQuery = new EventLogQuery("System", PathType.LogName, "*[System[(EventID=41 or EventID=1001)]]")
                {
                    ReverseDirection = true
                };
                using var sysReader = new EventLogReader(sysQuery);
                int count = 0;
                while (count < 30)
                {
                    var record = sysReader.ReadEvent();
                    if (record == null) break;
                    using (record)
                    {
                        var rep = ParseSystemCrashEvent(record);
                        if (rep != null)
                        {
                            reports.Add(rep);
                            count++;
                        }
                    }
                }
            }
            catch { }

            // 2. Quedas e Congelamentos de Aplicativos (Application: ID 1000 e ID 1002)
            try
            {
                var appQuery = new EventLogQuery("Application", PathType.LogName, "*[System[(EventID=1000 or EventID=1002)]]")
                {
                    ReverseDirection = true
                };
                using var appReader = new EventLogReader(appQuery);
                int count = 0;
                while (count < 30)
                {
                    var record = appReader.ReadEvent();
                    if (record == null) break;
                    using (record)
                    {
                        // Apenas falhas reais de programas (Application Error ou Application Hang)
                        if (record.ProviderName is not ("Application Error" or "Application Hang"))
                            continue;

                        var rep = ParseAppCrashEvent(record);
                        if (rep != null)
                        {
                            reports.Add(rep);
                            count++;
                        }
                    }
                }
            }
            catch { }

            return reports.OrderByDescending(r => r.Timestamp).Take(maxItems).ToList();
        });
    }

    private static CrashReport? ParseSystemCrashEvent(EventRecord record)
    {
        DateTime time = record.TimeCreated ?? DateTime.Now;
        string relative = FormatRelativeTime(time);
        string raw = "";
        try { raw = record.FormatDescription() ?? ""; } catch { }

        if (record.Id == 41) // Kernel-Power
        {
            long bugcheckCodeLong = 0;
            try
            {
                if (record.Properties != null && record.Properties.Count > 0)
                {
                    var val = record.Properties[0].Value;
                    if (val is long l) bugcheckCodeLong = l;
                    else if (val is int i) bugcheckCodeLong = (uint)i;
                    else if (val is string s && long.TryParse(s, out var p)) bugcheckCodeLong = p;
                }
            }
            catch { }

            uint code = (uint)bugcheckCodeLong;
            if (code != 0)
            {
                // É uma Tela Azul registrada pelo Kernel-Power
                var entry = FindBugcheck(code);
                return new CrashReport
                {
                    Timestamp = time,
                    RelativeTime = relative,
                    Type = "Tela Azul (BSOD)",
                    Title = $"Tela Azul · {entry.Name}",
                    StopCodeHex = $"0x{code:X8}",
                    StopCodeName = entry.Name,
                    CulpritCategory = entry.Culprit,
                    Explanation = entry.Explanation,
                    Recommendation = entry.Recommendation,
                    SeverityBadge = "CRÍTICO",
                    SeverityColor = "#FF453A",
                    RawDetails = raw
                };
            }
            else
            {
                // Queda de energia abrupta ou reinício forçado
                return new CrashReport
                {
                    Timestamp = time,
                    RelativeTime = relative,
                    Type = "Queda Inesperada",
                    Title = "O computador desligou ou reiniciou sem aviso",
                    StopCodeHex = "0x00000000",
                    StopCodeName = "DIRTY_SHUTDOWN",
                    CulpritCategory = "Energia / Desligamento Forçado",
                    Explanation = "O computador perdeu energia repentinamente, travou totalmente ou o botão Power foi segurado. Não houve tela azul com código gravado.",
                    Recommendation = "Verifique a estabilidade da tomada, cabos de força da fonte do PC e evite desligar segurando o botão de energia.",
                    SeverityBadge = "ALERTA",
                    SeverityColor = "#FF9F0A",
                    RawDetails = raw
                };
            }
        }
        else if (record.Id == 1001) // WER-SystemErrorReporting ou BugCheck
        {
            // Tenta extrair código em hex da mensagem
            uint code = 0;
            var match = Regex.Match(raw, @"0x[0-9a-fA-F]{1,8}");
            if (match.Success)
            {
                _ = uint.TryParse(match.Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
            }

            var entry = FindBugcheck(code);
            return new CrashReport
            {
                Timestamp = time,
                RelativeTime = relative,
                Type = "Tela Azul (BSOD)",
                Title = $"Relatório de Tela Azul · {entry.Name}",
                StopCodeHex = code != 0 ? $"0x{code:X8}" : "0x00000100",
                StopCodeName = entry.Name,
                CulpritCategory = entry.Culprit,
                Explanation = entry.Explanation,
                Recommendation = entry.Recommendation,
                SeverityBadge = "CRÍTICO",
                SeverityColor = "#FF453A",
                RawDetails = raw
            };
        }

        return null;
    }

    private static CrashReport? ParseAppCrashEvent(EventRecord record)
    {
        DateTime time = record.TimeCreated ?? DateTime.Now;
        string relative = FormatRelativeTime(time);
        string raw = "";
        try { raw = record.FormatDescription() ?? ""; } catch { }

        if (record.Id == 1000) // Application Error
        {
            string appName = "Aplicativo";
            string moduleName = "Desconhecido";
            uint exceptionCode = 0;

            try
            {
                if (record.Properties != null)
                {
                    if (record.Properties.Count > 0 && record.Properties[0]?.Value is string a) appName = a;
                    if (record.Properties.Count > 3 && record.Properties[3]?.Value is string m) moduleName = m;
                    if (record.Properties.Count > 6)
                    {
                        var exc = record.Properties[6]?.Value?.ToString() ?? "";
                        if (exc.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            _ = uint.TryParse(exc.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out exceptionCode);
                        else
                            _ = uint.TryParse(exc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out exceptionCode);
                    }
                }
            }
            catch { }

            // Se as propriedades falharem, usa Regex na mensagem bruta
            if (appName == "Aplicativo" && !string.IsNullOrWhiteSpace(raw))
            {
                var matchApp = Regex.Match(raw, @"Nome do aplicativo com falha:\s*([^\r\n,]+)", RegexOptions.IgnoreCase);
                if (matchApp.Success) appName = matchApp.Groups[1].Value.Trim();

                var matchMod = Regex.Match(raw, @"Nome do módulo com falha:\s*([^\r\n,]+)", RegexOptions.IgnoreCase);
                if (matchMod.Success) moduleName = matchMod.Groups[1].Value.Trim();

                var matchExc = Regex.Match(raw, @"Código de exceção:\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                if (matchExc.Success)
                    _ = uint.TryParse(matchExc.Groups[1].Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out exceptionCode);
            }

            if (appName.Length > 50 || appName.Contains('\n') || appName.Contains('\r'))
            {
                appName = Path.GetFileName(appName.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Aplicativo");
                if (appName.Length > 50) appName = appName.Substring(0, 47) + "...";
            }

            string excMeaning = ExplainExceptionCode(exceptionCode);
            string moduleOrigin = ClassifyModule(moduleName);

            string explanation = $"O aplicativo '{appName}' fechou abruptamente. A falha ocorreu no módulo '{moduleName}' ({moduleOrigin}).";
            string rec = moduleOrigin switch
            {
                "Driver de Placa de Vídeo" => "O travamento ocorreu diretamente no driver da sua placa de vídeo. Atualize ou reinstale o driver de vídeo.",
                "Biblioteca do Windows" => $"O erro aconteceu dentro de uma biblioteca do sistema ({moduleName}), geralmente disparado por passagem de dados inválidos pelo aplicativo '{appName}'.",
                _ => $"Verifique se há atualizações disponíveis para o '{appName}' ou reinstale o programa caso o travamento se repita."
            };

            return new CrashReport
            {
                Timestamp = time,
                RelativeTime = relative,
                Type = "Travamento de App (Crash)",
                Title = $"{appName} travou e fechou sozinho",
                StopCodeHex = $"0x{exceptionCode:X8}",
                StopCodeName = excMeaning,
                CulpritCategory = moduleOrigin,
                Explanation = explanation,
                Recommendation = rec,
                FaultingApp = appName,
                FaultingModule = moduleName,
                SeverityBadge = "ERRO",
                SeverityColor = "#FF6B6B",
                RawDetails = raw
            };
        }
        else if (record.Id == 1002) // Application Hang
        {
            string appName = "Aplicativo";
            try
            {
                if (record.Properties != null && record.Properties.Count > 0 && record.Properties[0]?.Value is string a)
                    appName = a;
            }
            catch { }

            if (appName == "Aplicativo" && !string.IsNullOrWhiteSpace(raw))
            {
                var m = Regex.Match(raw, @"O programa\s+([^\s]+)\s+versão", RegexOptions.IgnoreCase);
                if (m.Success) appName = m.Groups[1].Value.Trim();
            }

            return new CrashReport
            {
                Timestamp = time,
                RelativeTime = relative,
                Type = "App Congelado (Hang)",
                Title = $"{appName} congelou e parou de responder",
                StopCodeHex = null,
                StopCodeName = "APPLICATION_HANG",
                CulpritCategory = "Interface Bloqueada / Timeout",
                Explanation = $"O aplicativo '{appName}' parou de se comunicar com a interface gráfica do Windows e teve de ser finalizado.",
                Recommendation = "Geralmente ocorre quando o programa fica preso em um loop de processamento pesado ou aguardando resposta de rede/disco.",
                FaultingApp = appName,
                FaultingModule = null,
                SeverityBadge = "AVISO",
                SeverityColor = "#FFB84D",
                RawDetails = raw
            };
        }

        return null;
    }

    private static string ClassifyModule(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return "Desconhecido";
        string m = module.ToLowerInvariant();
        if (m.Contains("nvwgf2um") || m.Contains("nvlddmkm") || m.Contains("nvd3dum")) return "Driver de Placa de Vídeo (NVIDIA)";
        if (m.Contains("amdxx") || m.Contains("atikmdag") || m.Contains("atidxx")) return "Driver de Placa de Vídeo (AMD)";
        if (m.Contains("igd10") || m.Contains("igdumdim")) return "Driver de Placa de Vídeo (Intel)";
        if (m == "ntdll.dll" || m == "kernel32.dll" || m == "user32.dll" || m == "gdi32.dll" || m == "kernelbase.dll")
            return "Biblioteca do Windows";
        return "Arquivo Próprio / Outro";
    }

    /// <summary>Lista os arquivos minidump encontrados em C:\Windows\Minidump.</summary>
    public static List<MinidumpFile> GetMinidumpFiles()
    {
        var list = new List<MinidumpFile>();
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                foreach (var file in dir.GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTime))
                {
                    list.Add(new MinidumpFile(file.Name, file.FullName, file.LastWriteTime, file.Length));
                }
            }
        }
        catch { }
        return list;
    }
}
