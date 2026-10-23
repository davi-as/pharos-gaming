# Pharos Gaming

Central de desempenho para jogar no Linux: painel ao vivo para o segundo monitor (FPS, GPU, CPU, RAM e o que está
limitando o FPS), biblioteca de jogos com perfis, benchmarks, stress test, overclock, otimização do sistema, drivers,
iluminação RGB, a central do PC como console (controle, Hydra, Steam, TV) e ferramentas para quem programa Arduino e ESP32.

Fork do [RESET EXPLODE](https://github.com/GustavoReset/ResetExplode) (Windows/WPF), reescrito em Avalonia para rodar
nativo no Linux (X11 e Wayland). As 21 páginas do original foram portadas (mais Limitador de FPS, Sessões e PC como console); o código Windows continua em
[`legacy-windows/`](legacy-windows/) como referência.

## Páginas

**Painel**
- **Visão geral:** FPS, frametime, mínimo de 10 s e média da sessão; carga, clock e temperatura de GPU e CPU; VRAM e RAM;
  gráfico dos últimos 2 minutos e o gargalo de agora.
- **Sistema:** distro, kernel, placa-mãe e BIOS, CPU (caches, governador, boost), módulos de RAM com estimativa de
  EXPO/XMP e de canais, GPUs, monitores e discos. Exporta relatório em HTML ou JSON (sem números de série).
- **Sensores:** todos os chips hwmon (CPU, placa-mãe, NVMe, rede, amdgpu), clock e carga por thread, NVML e memória, com
  mínimo e máximo.
- **Placa de vídeo:** especificações no estilo GPU-Z (banco de dados próprio), PCIe, Resizable BAR, limites de energia,
  VBIOS, driver e sensores ao vivo.
- **Discos:** saúde pelo S.M.A.R.T. sem root (udisks) e leitura completa com `smartctl` (desgaste do SSD, total gravado).
- **Energia:** bateria (desgaste, ciclos, autonomia), perfil de energia e consumo dos sensores.
- **Rede:** latência, jitter e perda até o roteador e a internet, DNS, velocidade e bufferbloat.

**Jogos**
- **Biblioteca:** jogos da Steam (todas as bibliotecas, inclusive Flatpak) e adicionados à mão, com perfil por jogo:
  MangoHud, GameMode, modo de janela, resolução, Hz e argumentos. Grava as opções de inicialização da Steam (com backup)
  e abre programas auxiliares junto com o jogo.
- **FPS estimado:** quanto FPS esperar em jogos populares, pela API da HowManyFPS ou por estimativa local.
- **Gargalo:** equilíbrio CPU × GPU, veredito ao vivo e análise de uma sessão de jogo.
- **Overlay:** escolha o que o MangoHud mostra por cima do jogo, a posição e o tamanho. Ligado, o PHAROS grava o
  `MangoHud.conf` (vale para todo jogo com MangoHud, sem opção de inicialização) e guarda o seu; desligado, o seu volta.
- **Limitador de FPS:** limite por jogo com frame pacing (limitador do MangoHud no modo early), como o framepacer.
  Detecta o jogo aberto (Hydra, Steam ou qualquer launcher com MangoHud), sugere valores que casam com a taxa da tela,
  muda com o jogo aberto e dá um veredito do ritmo dos quadros. Shift + F1 no jogo alterna entre o limite e sem limite.
- **Sessões:** cada jogo que envia FPS vira uma sessão quando fecha (FPS médio, 1% low, ritmo dos quadros, temperaturas
  e cargas, limite em uso), comparada com a sessão anterior do mesmo jogo.

O FPS chega ao PHAROS mesmo com o overlay escondido: as configs que o PHAROS grava têm `control=` e ele liga o log pelo
socket do MangoHud (`:logging=1;`); para os jogos sem limite, ligue "FPS no PHAROS" em PC como console › MangoHud.
A ficha de cada jogo na Biblioteca mostra o limite e as últimas sessões: o PHAROS reconhece o processo na primeira vez
que vê o jogo rodando (os da Steam pelo `SteamAppId`; os do Hydra fora da Steam entram na lista).

**Console**
- **PC como console:** a central do ecossistema do `dotfiles/gaming` (o PC vira console: o Hydra é a interface e o
  controle liga o PC e a TV). Atalhos do controle (o que o Guide, Guide segurado, Guide + Start, View + Start e Share
  fazem, tempo de segurar, volume da TV no Guide + RB/LB), gravados em `~/.config/console/buttons.env` para o daemon do
  controle; Hydra (abrir com o sistema, MangoHud nos jogos), Steam com `MANGOHUD=1` (os jogos da Steam que o Hydra abre
  nascem da Steam), TV (`tv-power`), reconexão do controle por Bluetooth, MangoHud escondido no início e o diagnóstico
  do `gaming-doctor`. Cada arquivo editado ganha uma cópia `.pharos-bak` na primeira mudança.
- **Menu rápido na TV:** Guide + View no controle abre o Pharos em tela cheia por cima do jogo (o daemon do console
  devolve o jogo ao fechar). Mostra a sessão ao vivo (quanto do tempo ficou acima de 60, quedas, ritmo, temperaturas,
  a sessão anterior e a sugestão do jogo) e ajustes que valem com o jogo aberto: limite, geração de quadros, overlay,
  perfil de energia, captura e Big Picture. Navega pelo controle (direcional ou analógico, A muda, B volta ao jogo) ou
  pelas setas; também abre pela página e com `pharos-gaming --tv`.
- **Perfil da TV:** todo jogo novo que aparece na TV ganha o limite igual à taxa dela (59,95), e cada sessão mede o
  tempo abaixo de 60 e sugere o ajuste que falta (upscaling ou gráficos quando a GPU está cheia; CPU quando não está).
- **Geração de quadros (lsfg-vk):** por jogo, pelo menu rápido. O PHAROS escreve `~/.config/lsfg-vk/conf.toml` (um
  perfil por jogo, pelo `.exe`) e baixa o limite real para metade (29,98 × 2 = 59,95 na TV sem VRR). Precisa do
  lsfg-vk instalado e da Lossless Scaling no ramo beta `lsfg-vk` na Steam. Só como último recurso: dobra o atraso.

**Desempenho**
- **Benchmark:** render de CPU (path tracing, como o Cinebench), cena de jogo e ray tracing em OpenGL, simulação de jogo,
  memória/carregamento e disco (`O_DIRECT`, sem o cache do kernel).
- **Stress test:** CPU, RAM e GPU (pelo no estilo FurMark) com corte automático por temperatura.
- **Overclock:** limite de energia e teto de clock da GPU (NVIDIA e AMD), verificador de estabilidade que procura erros
  de hardware no log do kernel e guia de overclock na BIOS para a sua placa.

**Ferramentas**
- **Otimização:** fecha programas em segundo plano, devolve o cache de disco da RAM e aplica ajustes reversíveis (perfil
  Desempenho, `vm.max_map_count`, split lock, swappiness quando o swap é em disco, aceleração do mouse no GNOME).
- **Inicialização:** autostart XDG e serviços de usuário do systemd, com desligar, remover e restaurar.
- **Instalar apps:** Steam, MangoHud, GameMode, Discord, Lutris, Heroic, OBS e outros pelo pacman/apt/dnf (uma senha)
  ou Flatpak.
- **Drivers:** driver de cada dispositivo PCI, driver de vídeo (instalado × distro × NVIDIA), atualizações do sistema e
  de firmware (fwupd).
- **Integração:** iluminação RGB de tudo que o OpenRGB controla, com efeitos que seguem a temperatura ou a carga.

**Maker**
- **Portas seriais:** placas conectadas, acesso ao grupo `uucp`/`dialout`, ModemManager, brltty, programas que prendem a
  porta e nomes fixos em `/dev/serial/by-id`.
- **Drivers seriais:** módulos do kernel de CH340, CP210x, FTDI, Prolific e CDC; Arduino IDE.

As ações que mexem no sistema pedem a senha pelo `pkexec`, uma vez por ação, e quase todas têm "restaurar".

## Configurar o FPS (MangoHud)
O FPS vem do log do [MangoHud](https://github.com/flightlessmango/MangoHud) (`pacman -S mangohud`, `apt install mangohud`...).

- **Pela Biblioteca:** escolha o jogo, deixe "Mandar o FPS para o painel" marcado e clique em APLICAR NAS OPÇÕES DA STEAM.
- **À mão:** cole nas opções de inicialização do jogo (Steam › Propriedades › Opções de inicialização) a linha que o
  painel mostra:
  ```
  MANGOHUD_CONFIG="read_cfg,autostart_log=1,log_interval=250,output_folder=$HOME/.local/share/pharos-gaming/mangohud" mangohud %command%
  ```
  O `read_cfg` no começo é o que mantém o seu `MangoHud.conf` (sem ele, o MangoHud ignora o arquivo).

Já usa o MangoHud com log ligado? O PHAROS também lê a `output_folder` do seu `~/.config/MangoHud/MangoHud.conf`.

## Instalar (atalho no menu de aplicativos)
```
git clone https://github.com/davi-as/pharos-gaming.git
cd pharos-gaming
scripts/install.sh
```
Publica o app self-contained em `~/.local/share/pharos-gaming/app` (abre sem o .NET instalado), instala o ícone e cria o
atalho "Pharos Gaming" no menu de aplicativos (no Omarchy, Super + Espaço). Rode de novo depois de atualizar o código;
`scripts/install.sh --uninstall` remove (as configurações em `~/.config/pharos-gaming` ficam).

## Identidade visual
A mesma do [Pharos](https://github.com/davi-as/pharos) (`docs/DESIGN.md` de lá): o Φ do Farol de Alexandria, tema claro
"papiro" e escuro "noite do Nilo" seguindo o tema do sistema, lápis-lazúli no que age, ouro na luz, turquesa no ok e
cornalina no alerta; títulos e números em Literata, texto em Fira Sans (embutidas em `src/Assets/Fonts`, licença OFL).

## Rodar a partir do código
Precisa do .NET SDK 10. Com o [mise](https://mise.jdx.dev):
```
git clone https://github.com/davi-as/pharos-gaming.git
cd pharos-gaming
mise install
mise exec -- dotnet run --project src
```
Sem o mise, instale o `dotnet-sdk` 10 pela sua distro e rode `dotnet run --project src`.

Usa ferramentas que as distros já trazem: `lspci`, `lsblk`, `udevadm`, `journalctl`, `pkexec`. Opcionais: `smartctl`
(Discos), `nvidia-smi` (Overclock), `powerprofilesctl`, `fwupdmgr`, `openrgb`, `flatpak`.

## Testes
```
mise exec -- dotnet test tests
```
100 testes xunit: os parsers (sysfs, udev, lspci, lsblk, smartctl, journal, MangoHud, VDF da Steam), os cálculos (gargalo,
EXPO, frametime, efeitos de luz), os scripts de root (aspas e conteúdo), o protocolo do OpenRGB contra um servidor falso,
a leitura desta máquina e a interface inteira em modo headless (abre as 24 páginas e o modo TV). Os testes de GPU rodam a cena, o ray
tracing e o stress por um instante quando há GPU com aceleração, e passam sem rodar quando não há (CI). O CI do GitHub
roda a suíte no Ubuntu a cada push.

Capturas de todas as páginas, para revisar a interface: `PHAROS_SHOTS=pasta dotnet test tests --filter Screenshots`
(com `PHAROS_SHOTS_THEME=dark` ou `light` para escolher o tema).

## Diferenças em relação à versão Windows
- **Ray tracing:** usa um compute shader OpenGL (raios calculados pelos shaders). Os núcleos RT exigiriam Vulkan com ray
  query; a nota não se compara com a do Windows.
- **Overlay:** é o próprio MangoHud, configurado pela página.
- **Ícones:** [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0), embutidos em `src/Icons.axaml`.
- **Lojas:** a Biblioteca lê a Steam e jogos adicionados à mão (Epic e GOG ficam com o Heroic, Battle.net com o Lutris).
- **Iluminação:** só pelo OpenRGB, que no Linux também cobre Corsair e Razer.
- **Drivers:** o kernel já traz quase todos; a página mostra o que está em uso e as atualizações, sem instaladores de fabricante.

## Estrutura
```
src/             app Avalonia (.NET 10): uma página por arquivo em src/Pages
tests/           testes xunit + Avalonia.Headless
legacy-windows/  código original do RESET EXPLODE, referência
```

## Licença
[MIT](LICENSE). Mantenha o aviso de copyright do projeto original.
