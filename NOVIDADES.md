## Novidades da Edição Araújo Control Suite (v0.16)

**Rebranding Completo · ARAÚJO CONTROL SUITE**
- Nova identidade visual (ícone Squircle e banner oficial), executável `AraujoControlSuite.exe` e integração total das ferramentas do notebook em uma única interface.

**Nova página · DESEMPENHO › G15 Control**
- **GPU Tuning (RTX 3060 · MSI Afterburner)**:
  - **Perfil 1 (Otimizado · Undervolt)**: curva V/F travada em `850 mV @ 1860 MHz` e `+500 MHz` na VRAM.
  - **Perfil 2 (Compatibilidade)**: curva em `1000 mV` com boost de `+950 MHz` no Core.
  - Sliders azuis no padrão visual do Afterburner com exibição ao vivo de `mV` e `MHz` nos dois perfis.
- **CPU Tuning (Ryzen 7 5800H · SMU Direct)**:
  - Controle direto dos registradores SMU via PawnIO (`88 °C` limite térmico, `35 W` STAPM/Slow, `42 W` Fast Boost) e sincronização com o preset do UXTU.
- **Refrigeração & G-Mode (Dell G15 AlienFan)**:
  - Monitoramento ao vivo de temperaturas e RPM das ventoinhas CPU/GPU.
  - Chaveamento rápido entre **Modo Adaptativo Inteligente (Curva 8 Pontos)** e **G-Mode (Força Bruta 100%)**.
- **Iluminação do Teclado (AlienFX 4 Zonas)**:
  - Caixa de cor RGB HEX (`#RRGGBB`) com preview em tempo real, presets rápidos e slider de brilho (`0–100%`).
- **Automação & Inicialização com o Windows**:
  - Criação automática da tarefa agendada `\AraujoControlSuite` com privilégio máximo, opção de iniciar minimizado e aplicação automática de todas as otimizações no login.
