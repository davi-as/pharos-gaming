# PHAROS GAMING

Painel de desempenho para Linux, feito para ficar aberto num segundo monitor enquanto você joga. Mostra ao vivo o FPS
do jogo, o uso de GPU, CPU e memória, e diz o que está limitando o seu FPS.

Fork do [RESET EXPLODE](https://github.com/GustavoReset/ResetExplode) (Windows/WPF), reescrito em Avalonia para rodar
nativo no Linux (X11 e Wayland).

## O que mostra
- **FPS:** atual, frametime, mínimo dos últimos 10 s e média da sessão.
- **GPU:** carga, clock, temperatura e VRAM.
- **CPU:** carga total e da thread mais ocupada, clock e temperatura.
- **RAM:** uso.
- **Gráfico:** carga de GPU e CPU nos últimos 2 minutos.
- **Gargalo agora:** GPU no limite, gargalo de CPU, VRAM cheia, RAM cheia, limitado por temperatura ou nada no limite
  (trava de FPS/V-Sync), com uma dica do que fazer em cada caso.
- **Sessão de jogo:** quanto tempo cada componente passou sendo o limite.

## Aba Jogos
Lista os jogos da Steam (todas as bibliotecas, inclusive Flatpak) e os que você adicionar à mão. Cada jogo tem um perfil:
MangoHud (manda o FPS para o painel), GameMode, modo de janela, resolução, Hz e argumentos extras. Para jogos da Steam, o
PHAROS grava a linha nas opções de inicialização do jogo (com backup do `localconfig.vdf`; a Steam precisa ser fechada).
Também abre programas auxiliares antes do jogo e os fecha quando ele termina.

## Configurar o FPS (MangoHud)
O FPS vem do log do [MangoHud](https://github.com/flightlessmango/MangoHud), que precisa estar instalado
(`pacman -S mangohud`, `apt install mangohud`, etc.).

1. Abra o PHAROS. Quando nenhum jogo estiver enviando FPS, ele mostra uma opção de inicialização pronta para copiar,
   parecida com:
   ```
   MANGOHUD_CONFIG="autostart_log=1,log_interval=250,output_folder=$HOME/.local/share/pharos-gaming/mangohud" mangohud %command%
   ```
2. Na Steam: botão direito no jogo › Propriedades › Opções de inicialização › cole.
3. Abra o jogo. O painel começa a mostrar o FPS em poucos segundos.

Já usa o MangoHud com log ligado (atalho ou `autostart_log` no `MangoHud.conf`)? O PHAROS também lê a `output_folder` do
seu `~/.config/MangoHud/MangoHud.conf`, então não precisa mudar nada.

## Rodar
Precisa do .NET SDK 10. Com o [mise](https://mise.jdx.dev):
```
git clone https://github.com/davi-as/pharos-gaming.git
cd pharos-gaming
mise install
mise exec -- dotnet run --project src
```
Sem o mise, instale o `dotnet-sdk` 10 pelo gerenciador da sua distro e rode `dotnet run --project src`.

Testes: `dotnet test tests`.

## De onde vêm os dados
Nada além do .NET e do Avalonia: o PHAROS lê direto do sistema.

| Dado | Fonte |
|------|-------|
| FPS e frametime | CSV do MangoHud |
| GPU NVIDIA | NVML (`libnvidia-ml.so.1`, vem com o driver) |
| GPU AMD | sysfs do `amdgpu` |
| CPU | `/proc/stat`, `cpufreq`, `hwmon` (k10temp, zenpower, coretemp) |
| RAM | `/proc/meminfo` |

## Limitações
- A leitura de GPU AMD ainda não foi testada em hardware real. GPU Intel ainda não tem suporte.
- O "mínimo 10 s" usa as médias de intervalo do log do MangoHud, não o 1% low real.
- Os logs do MangoHud se acumulam em `~/.local/share/pharos-gaming/mangohud` e não são apagados sozinhos.

## Estrutura
```
src/             app Avalonia (.NET 10)
tests/           testes xunit
legacy-windows/  código original do RESET EXPLODE, referência para portar
```

## Próximos módulos
Os outros módulos do original estão sendo portados um por vez: Bench, Otimiza, Driver, OC, Maker e
Integração. Enquanto isso, o código Windows continua em [`legacy-windows/`](legacy-windows/).

## Licença
[MIT](LICENSE). Mantenha o aviso de copyright do projeto original.
