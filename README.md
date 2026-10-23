# PHAROS GAMING

Central de desempenho para jogar no Linux: painel ao vivo para o segundo monitor (FPS, GPU, CPU, RAM e o que está
limitando o FPS), biblioteca de jogos com perfis, benchmarks, stress test, overclock, otimização do sistema, drivers,
iluminação RGB e ferramentas para quem programa Arduino e ESP32.

Fork do [RESET EXPLODE](https://github.com/GustavoReset/ResetExplode) (Windows/WPF), reescrito em Avalonia para rodar
nativo no Linux (X11 e Wayland). As 21 páginas do original foram portadas; o código Windows continua em
[`legacy-windows/`](legacy-windows/) como referência.

## Páginas

**PHAROS GAMING**
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

**PHAROS JOGOS**
- **Biblioteca:** jogos da Steam (todas as bibliotecas, inclusive Flatpak) e adicionados à mão, com perfil por jogo:
  MangoHud, GameMode, modo de janela, resolução, Hz e argumentos. Grava as opções de inicialização da Steam (com backup)
  e abre programas auxiliares junto com o jogo.
- **FPS estimado:** quanto FPS esperar em jogos populares, pela API da HowManyFPS ou por estimativa local.
- **Gargalo:** equilíbrio CPU × GPU, veredito ao vivo e análise de uma sessão de jogo.
- **Overlay:** escolha o que o MangoHud mostra por cima do jogo, a posição e o tamanho (arquivo próprio do PHAROS; o seu
  `MangoHud.conf` não é tocado).

**PHAROS DESEMPENHO**
- **Benchmark:** render de CPU (path tracing, como o Cinebench), cena de jogo e ray tracing em OpenGL, simulação de jogo,
  memória/carregamento e disco (`O_DIRECT`, sem o cache do kernel).
- **Stress test:** CPU, RAM e GPU (pelo no estilo FurMark) com corte automático por temperatura.
- **Overclock:** limite de energia e teto de clock da GPU (NVIDIA e AMD), verificador de estabilidade que procura erros
  de hardware no log do kernel e guia de overclock na BIOS para a sua placa.

**PHAROS FERRAMENTAS**
- **Otimização:** fecha programas em segundo plano, devolve o cache de disco da RAM e aplica ajustes reversíveis (perfil
  Desempenho, `vm.max_map_count`, split lock, swappiness quando o swap é em disco, aceleração do mouse no GNOME).
- **Inicialização:** autostart XDG e serviços de usuário do systemd, com desligar, remover e restaurar.
- **Instalar apps:** Steam, MangoHud, GameMode, Discord, Lutris, Heroic, OBS e outros pelo pacman/apt/dnf (uma senha)
  ou Flatpak.
- **Drivers:** driver de cada dispositivo PCI, driver de vídeo (instalado × distro × NVIDIA), atualizações do sistema e
  de firmware (fwupd).
- **Integração:** iluminação RGB de tudo que o OpenRGB controla, com efeitos que seguem a temperatura ou a carga.

**PHAROS MAKER**
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

## Rodar
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
72 testes xunit: os parsers (sysfs, udev, lspci, lsblk, smartctl, journal, MangoHud, VDF da Steam), os cálculos (gargalo,
EXPO, frametime, efeitos de luz), os scripts de root (aspas e conteúdo), o protocolo do OpenRGB contra um servidor falso,
a leitura desta máquina e a interface inteira em modo headless (abre as 21 páginas). Os testes de GPU rodam a cena, o ray
tracing e o stress por um instante quando há GPU com aceleração, e passam sem rodar quando não há (CI). O CI do GitHub
roda a suíte no Ubuntu a cada push.

## Diferenças em relação à versão Windows
- **Ray tracing:** usa um compute shader OpenGL (raios calculados pelos shaders). Os núcleos RT exigiriam Vulkan com ray
  query; a nota não se compara com a do Windows.
- **Overlay:** é o próprio MangoHud, configurado pela página.
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
