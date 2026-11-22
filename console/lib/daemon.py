#!/usr/bin/env python3
"""console daemon: controle Xbox / 8BitDo -> Hydra Big Picture, TV, MangoHud, suspender.

Reage a eventos: o hotplug vem do netlink do udev (controle novo abre na hora), os botões
vêm do evdev. A lógica dos botões fica em buttons.py (com testes); as ações rodam numa
thread separada, então a leitura dos controles nunca trava.
Valores da máquina (MACs, IP da TV) ficam em ~/.config/console/config.env; o que cada gesto do
controle faz fica em ~/.config/console/buttons.env (editado pelo PHAROS GAMING, que reinicia o serviço).
"""
import fcntl, importlib.machinery, importlib.util, os, queue, re, select, signal, socket, struct, subprocess, threading, time

import buttons, hydraos, hypr, neolog

NAMES = ("Xbox Wireless Controller", buttons.PRO)  # IMU do 8BitDo ("... (IMU)") fica de fora pelo match exato
HOME = os.path.expanduser("~")
BIN = f"{HOME}/.local/bin"
HYDRA = f"{BIN}/hydra"
HYDRA_BIN = f"{HOME}/.local/opt/hydra-neo/usr/bin/hydra"  # Hydra Neo (nativo)
TICK = 3  # rescan de segurança + checagem de resume quando nada acontece

LOG = f"{HOME}/.local/state/xbox-guide/guide-press.log"
os.makedirs(os.path.dirname(LOG), exist_ok=True)
try:
    if os.path.getsize(LOG) > 1 << 20:  # cresce a cada aperto: corta no boot do daemon
        os.replace(LOG, LOG + ".old")
except OSError:
    pass


def log(msg):
    with open(LOG, "a") as f:
        f.write(f"{time.strftime('%H:%M:%S')} {msg}\n")


def spawn(*argv):
    with open(LOG, "a") as err:
        subprocess.Popen(argv, start_new_session=True, stdout=subprocess.DEVNULL, stderr=err)


def notify(msg, ms):
    # id fixo: o aviso "continue segurando" é substituído em vez de empilhar
    subprocess.Popen(["notify-send", "-a", "Controle", "-t", str(ms), "-r", "31690", msg],
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# --- teclado virtual: View+Start -> Ctrl+Shift+Q (toggle do MangoHud) ---
KCTRL, KSHIFT, KQ = 29, 42, 16
UI_SET_EVBIT, UI_SET_KEYBIT, UI_DEV_CREATE = 0x40045564, 0x40045565, 0x5501


class VKbd:
    """Teclado uinput fixo (criado no boot do daemon, nunca some): zero churn de hotplug."""

    def __init__(self):
        self.fd = None
        try:
            fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
        except OSError:
            return  # sem uinput o chord não faz nada; Ctrl+Shift+Q no teclado segue ok
        try:
            # device info ANTES dos SET_*BIT/DEV_CREATE (uinput moderno exige)
            os.write(fd, struct.pack("80sHHHHi1024x", b"MangoHud Toggle VKbd", 0x03, 0x1, 0x1, 1, 0))
            fcntl.ioctl(fd, UI_SET_EVBIT, 1)  # EV_KEY
            for k in (KCTRL, KSHIFT, KQ):
                fcntl.ioctl(fd, UI_SET_KEYBIT, k)
            fcntl.ioctl(fd, UI_DEV_CREATE)
            self.fd = fd
        except OSError:
            os.close(fd)

    def chord(self, keys=(KCTRL, KSHIFT, KQ)):
        if self.fd is None:
            return
        # humanizado: cada tecla no seu SYN com micro-pausas. Down em bloco com 34 us de
        # hold não é visto por quem amostra o estado por evento.
        for k in keys:
            os.write(self.fd, struct.pack("qqHHi", 0, 0, 1, k, 1) + struct.pack("qqHHi", 0, 0, 0, 0, 0))
            time.sleep(0.012)
        time.sleep(0.09)
        for k in reversed(keys):
            os.write(self.fd, struct.pack("qqHHi", 0, 0, 1, k, 0) + struct.pack("qqHHi", 0, 0, 0, 0, 0))
            time.sleep(0.012)


vkbd = VKbd()


def mangohud_sockets():
    """Sockets de controle do MangoHud abertos agora (control= na config; o PHAROS põe nos arquivos por jogo)."""
    try:
        with open("/proc/net/unix") as f:
            return sorted({line.split()[-1][1:] for line in f if line.split()[-1].startswith("@mangohud-")})
    except OSError:
        return []


def mangohud_toggle():
    """View + Start: com o Pharos --tv aberto, SIGUSR2 e ele regrava a config do jogo (no_display), o que vale em
    qualquer jogo. Sem ele, o socket (":hud;"; nos jogos DX12/vkd3d ninguém aceita a conexão) e o Ctrl+Shift+Q."""
    pid = pharos_pid()
    if pid:
        log(f"mangohud: alterna pelo Pharos ({pid})")
        os.kill(pid, signal.SIGUSR2)
        return
    sent = 0
    for name in mangohud_sockets():
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                s.settimeout(0.5)
                s.connect("\0" + name)
                s.sendall(b":hud;")
                time.sleep(0.2)  # o MangoHud lê o socket uma vez por quadro
            sent += 1
        except OSError:
            pass
    log(f"mangohud: :hud; em {sent} socket(s)" if sent else "mangohud: sem socket, atalho de teclado")
    if not sent:
        vkbd.chord()

# --- volume da TV pela conexão do próprio daemon: um tv-power por clique pagava ~1s de
# TCP+TLS+handshake. A conexão fecha após VOL_IDLE parado (a TV derruba socket ocioso).
_loader = importlib.machinery.SourceFileLoader("tv_power", f"{BIN}/tv-power")
tvp = importlib.util.module_from_spec(importlib.util.spec_from_loader("tv_power", _loader))
_loader.exec_module(tvp)
VOL_IDLE = 5
vol_q = queue.Queue()


def vol_worker():
    sock = None
    while True:
        try:
            key = vol_q.get(timeout=VOL_IDLE)
        except queue.Empty:
            if sock:
                sock.close()
                sock = None
            continue
        for retry in (False, True):  # socket velho que a TV fechou -> reconecta uma vez
            try:
                sock = sock or tvp.ws_connect(3)
                tvp.click(sock, key)
                break
            except (OSError, ValueError) as e:
                if sock:
                    sock.close()
                sock = None
                if retry:
                    log(f"volume {key}: {e!r}")


# --- ações demoradas rodam em série nesta thread, nunca no loop que lê os controles ---
actions = queue.Queue()


def action_worker():
    while True:
        fn, args = actions.get()
        try:
            fn(*args)
        except Exception as e:  # uma ação quebrada não derruba o daemon
            log(f"ação {fn.__name__} falhou: {e!r}")


def act(fn, *args):
    actions.put((fn, args))


# --- Hydra Big Picture ---

# Janela do BP rastreada pelo ENDEREÇO e pelo título (o do Neo é fixo): o Hyprland só deixa uma janela
# fullscreen por workspace, então o BP perde o fullscreen quando o jogo ganha foco e continua vivo.
bp_addr = None
WARM_WS = "special:hydrabp"  # BP aquecido fica escondido aqui até o Guide
BP_TITLE = hydraos.BP_TITLE
warming = False  # warm_bp está abrindo o BP escondido: o openwindow dele não ganha foco
bp_wanted_at = float("-inf")  # alguém pediu o BP agora (Guide, fim de jogo): o warm_bp não esconde esse


def is_bp(c):
    return c.get("class") == "hydralauncher" and c.get("title") == BP_TITLE


def bp_window(cs):
    """Endereço do BP vivo (em tela cheia, atrás do jogo ou aquecido no WARM_WS), ou None."""
    global bp_addr
    bp_addr = next((c["address"] for c in cs if is_bp(c)), None)
    return bp_addr


def focus(addr, fullscreen):
    """Foca e deixa a janela com tela cheia ou sem, explicitamente: o Hyprland passa a tela
    cheia do BP para a janela que ganha foco, e uma janela normal viraria tela cheia."""
    hypr.dispatch(f'hl.dsp.focus({{window="address:{addr}"}})')
    action = "set" if fullscreen else "unset"  # sem action o dispatch alterna
    hypr.dispatch(f'hl.dsp.window.fullscreen({{ mode = "fullscreen", action = "{action}" }})')


def screen(direction):
    """Guide + D-pad: foca o monitor do workspace seguinte/anterior, em ciclo. Não usa a direção
    espacial do Hyprland: a TV fica acima do monitor da HDMI, sem vizinho à esquerda nem à direita."""
    mons = sorted(hypr._json("j/monitors") or [], key=lambda m: m["activeWorkspace"]["id"])
    cur = next((i for i, m in enumerate(mons) if m["focused"]), None)
    if cur is None:
        return
    mon = mons[(cur + (1 if direction == "r" else -1)) % len(mons)]
    hypr.dispatch(f'hl.dsp.focus({{ monitor = "{mon["name"]}" }})')
    notify(f"Tela {mon['activeWorkspace']['name']} · {mon['name']}", 1200)


def send_signal():
    """Manda --big-picture para o Hydra Neo que já está rodando: o próprio binário repassa à instância única
    (socket abstrato @hydra-native-*.sock) e sai em ~0,1 s. True se repassou."""
    if not hydra_main():
        return False  # sem instância, o comando abriria um Hydra novo dentro do cgroup do daemon
    try:
        r = subprocess.run([HYDRA, "--big-picture"], capture_output=True, timeout=3)
    except subprocess.TimeoutExpired:
        return False
    return b"handed the launch over" in r.stdout + r.stderr


def press():
    """Guide: de fora, leva ao Hydra OS (área 4 da TV) no último estado; de dentro, alterna jogo e BP.
    A decisão é do hydraos.HydraOS (testado); aqui só se junta o que o Hyprland mostra agora."""
    cs, tv = hypr.clients() or [], tv_monitor()
    inside = bool(tv and tv["focused"] and tv["activeWorkspace"]["name"] == GAME_WS)
    bp = bp_window(cs)
    game = hos.game if any(c["address"] == hos.game for c in cs) else None
    log(f"press: {'dentro' if inside else 'fora'} do Hydra OS, BP={bp} jogo={game} fase={hos.phase}")
    run_hos(hos.guide(inside, hypr.active(), bp, game, time.monotonic()))


# Jogos cuja regra fullscreen do Hyprland perde para o BP já em tela cheia no workspace da TV
# (uma tela cheia por workspace; o suppress_event impede o jogo de pedir depois).
FS_ON_OPEN = {"eldenring.exe"}


# --- workspace 4: só Hydra e jogos, na TV (regras visuais em ~/.config/hypr/hyprland.lua) ---
GAME_WS = "4"
TV_DESC = "Samsung Electric Company SAMSUNG 0x01000E00"
# classes que podem ficar no 4; além delas, qualquer janela cujo processo descende do Hydra
GAME_CLASS = re.compile(r"(?i)^(hydralauncher|steam_app_.*|.*\.exe|gamescope|towerfall|shadps4|(dev\.eden_emu\.)?eden"
                        r"|rpcs3|pcsx2.*|duckstation.*|dolphin-emu|(com\.libretro\.)?retroarch)$")
OMARCHY_SHELL = "/usr/share/omarchy/bin/omarchy-shell"
DND_PREV = f"{HOME}/.local/state/xbox-guide/dnd-before-ws4"  # estado do "não perturbe" antes de entrar no 4
BAR_FLAG = f"{HOME}/.local/state/omarchy/toggles/bar-off"  # o Omarchy esconde a barra (todas as telas) com ele
BAR_MARK = f"{HOME}/.local/state/xbox-guide/bar-hidden-by-hydraos"  # fomos nós que escondemos: devolver ao sair


BACKDROP = f"{BIN}/hydraos-backdrop"
hos = hydraos.HydraOS()  # estado do Hydra OS; só a thread das ações mexe nele (act)


def tv_monitor():
    return next((m for m in hypr._json("j/monitors") or [] if m.get("description") == TV_DESC), None)


def from_hydra(pid):
    """O processo (ou um ancestral) é o Hydra: jogos e emuladores abertos por ele."""
    for _ in range(64):
        if not pid or pid <= 1:
            return False
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                if f.read().split(b"\0")[0] == HYDRA_BIN.encode():
                    return True
            with open(f"/proc/{pid}/stat") as f:
                pid = int(f.read().rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            return False
    return False


def game_window(c):
    return bool(GAME_CLASS.match(c.get("class", "") or c.get("initialClass", ""))) or from_hydra(c.get("pid"))


# shortcut: títulos de janela da Steam que NÃO são diálogo; a lista cresce conforme aparecerem diálogos novos
STEAM_NOT_DIALOG = {"", "Steam", "Launching...", "Special Offers", "Friends List"}
dialogs = set()  # diálogos da Steam segurados na área 4 durante o carregamento


def expel(addr):
    """Janela que não é jogo caiu no 4 -> vai para o 3 (desktop da TV), sem levar o foco. Exceção: diálogo da Steam
    durante o carregamento (conflito de nuvem etc.) trava o jogo se sumir: fica no centro, por cima do preto."""
    c = next((c for c in hypr.clients() or [] if c["address"] == addr), None)
    if not c or c["workspace"]["name"] != GAME_WS or game_window(c):
        return
    title = c.get("title") or ""
    # notificationtoasts_*: aviso da Steam ("controle conectado"), não diálogo; vai para a 3 como o resto
    if (hos.phase == "launching" and c.get("class") == "steam" and title not in STEAM_NOT_DIALOG
            and not title.startswith("notificationtoasts")):
        log(f"Hydra OS: diálogo da Steam no carregamento fica na área {GAME_WS}: {c.get('title')!r}")
        dialogs.add(addr)
        hypr.dispatch(f'hl.dsp.window.float({{ window = "address:{addr}", action = "set" }})')
        hypr.dispatch(f'hl.dsp.window.center({{ window = "address:{addr}" }})')
        hypr.dispatch(f'hl.dsp.focus({{window="address:{addr}"}})')
        run_hos(hos.window(addr, "dialog", True, time.monotonic()))
        return
    log(f"ws{GAME_WS}: {c.get('class')} não é jogo -> workspace 3")
    hypr.dispatch(f'hl.dsp.window.move({{ workspace = "3", window = "address:{addr}", follow = false }})')
    notify(f"Workspace {GAME_WS} é só para jogos: {c.get('class') or c.get('title')} foi para o 3", 2500)


# --- Hydra OS: tela base, perfil e as ações do hydraos.HydraOS ---
# Tela base (hydraos-backdrop): "base" = preto por baixo das janelas (cobre o papel de parede) enquanto a TV mostra a
# área 4; "cover" = preto também por cima da barra e de janelas soltas, só sem nada em tela cheia na área 4 (o
# intervalo do lançamento e do fim do jogo). Não depende do Hyprland esconder camadas atrás da tela cheia, que
# falhou para camada nova (detalhes no hydraos-backdrop).
backdrop = None  # Popen do hydraos-backdrop
bd_spawned = float("-inf")
bd_hold = False  # diálogo da Steam no carregamento: transparente enquanto ele existir
bd_sent = None  # último estado mandado: off | base | cover
bd_text = ""
bd_art = ("", "")  # (fundo, logo) do jogo na tela de carregamento
bd_sub = ""  # 2ª linha da tela de carregamento ("Preparando na Steam…")
steam_launching = None  # janela "Launching..." da Steam aberta agora (shaders, nuvem, atualização antes do jogo)
SPLASH_SECONDS = 1.4  # abertura com a marca do Hydra ao entrar vindo da mesa ou do PC acordando
# BP por cima do jogo (área especial): esmaece como o menu Home de um console; fora do Hydra OS, o padrão do Omarchy
SPECIAL_ANIM_CONSOLE = 'hl.animation({ leaf = "specialWorkspace", enabled = true, speed = 2.5, bezier = "easeOutQuint", style = "fade" })'
SPECIAL_ANIM_DESK = 'hl.animation({ leaf = "specialWorkspace", enabled = true, speed = 3, bezier = "easeOutQuint", style = "slidevert" })'

ART_TOOL = f"{HOME}/.local/share/hydra-art/custom-games.mjs"  # "art": nome/fundo/logo dos jogos personalizados


def backdrop_ensure(tv):
    """Sobe o hydraos-backdrop se ele não está rodando. False se não deu (TV fora, ou morreu há pouco)."""
    global backdrop, bd_spawned, bd_sent
    if backdrop and backdrop.poll() is None:
        return True
    if time.monotonic() - bd_spawned < 10:
        return False  # morreu logo depois de subir: não fica reabrindo a cada evento
    bd_spawned = time.monotonic()
    with open(LOG, "a") as err:
        backdrop = subprocess.Popen([BACKDROP, "--monitor", TV_DESC], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                    stderr=err, text=True, start_new_session=True)
    bd_sent = None
    log("tela base aberta")
    return backdrop_send(f"text {bd_text}") and backdrop_send("art " + "\t".join(bd_art)) and backdrop_send(f"sub {bd_sub}")


def backdrop_send(line):
    global backdrop
    try:
        backdrop.stdin.write(line + "\n")
        backdrop.stdin.flush()
        return True
    except (AttributeError, OSError, ValueError):
        backdrop = None
        return False


def sync_tv():
    """Tela base preta enquanto a TV mostra a área 4; perfil do Hydra OS enquanto, além disso, a TV tem o foco."""
    global bd_sent
    tv = tv_monitor()
    visible = bool(tv and tv["activeWorkspace"]["name"] == GAME_WS and tv.get("dpmsStatus", True))
    if tv and not visible and tv["focused"] and bp_overlay_shown(tv):
        toggle_bp_overlay()  # saiu do Hydra OS com o BP por cima do jogo: ele não vai junto para a área 3
    if tv and backdrop_ensure(tv):
        ws4 = [c for c in hypr.clients() or [] if c["workspace"]["name"] == GAME_WS]
        want = hydraos.backdrop_state(visible, ws4, bd_hold, launching=hos.phase == "launching")
        if want != bd_sent and backdrop_send(want):
            log(f"tela base: {want} (fase {hos.phase}, área 4: {[c.get('class') for c in ws4]})")
            bd_sent = want
        # a área especial abre no monitor em foco: só com a TV focada
        if visible and tv["focused"] and any(c.get("fullscreen") for c in ws4) and not bp_overlay_shown(tv):
            unstick_layers(tv)
    profile(visible and tv["focused"])


def unstick_layers(tv):
    """Camada presa por cima do jogo/BP em tela cheia (a barra do Omarchy depois de a TV religar, Hyprland 0.56.2):
    abrir e fechar uma área especial vazia faz o Hyprland refazer a conta e esconder a camada atrás da tela cheia.
    Testado: sem piscar e sem tirar o foco do jogo. Só quando há camada presa."""
    stuck = hydraos.stuck_top_layers(hypr.request("layers") or "", tv["name"])
    if stuck:
        log(f"Hydra OS: {', '.join(stuck)} por cima da tela cheia -> refaz a ordem das camadas")
        hypr.dispatch('hl.dsp.workspace.toggle_special("hydrafix")')
        hypr.dispatch('hl.dsp.workspace.toggle_special("hydrafix")')


def bar(hide):
    """Barra do Omarchy: some enquanto o Hydra OS está ativo. Com ela, todo BP fora de tela cheia (o Neo tira a
    tela cheia dele ao lançar um jogo, até o jogo abrir) e todo jogo que abre em janela mostravam a barra na TV.
    Barra que você mesmo escondeu (sem a nossa marca) não é mexida."""
    if hide and not os.path.exists(BAR_FLAG):
        subprocess.run(["omarchy-toggle", "bar-off", "on"], capture_output=True, timeout=3)
        open(BAR_MARK, "w").close()
    elif not hide and os.path.exists(BAR_MARK):
        subprocess.run(["omarchy-toggle", "bar-off", "off"], capture_output=True, timeout=3)
        os.remove(BAR_MARK)
    else:
        return
    # o watch do shell pode perder trocas rápidas do arquivo (omarchy-toggle-bar faz o mesmo)
    subprocess.run([OMARCHY_SHELL, "-q", "omarchy.bar", "syncHidden"], capture_output=True, timeout=3)


def profile(active):
    """Hydra OS ativo: 'não perturbe', sem a barra do Omarchy e sem o diálogo 'não está respondendo' do Hyprland (um
    jogo compilando shaders mostraria ele na TV). Ao sair, tudo volta: DND e barra ao estado de antes, ANR a true."""
    try:
        if active and not os.path.exists(DND_PREV):
            prev = subprocess.run([OMARCHY_SHELL, "notifications", "dndState"], capture_output=True, text=True, timeout=3).stdout.strip()
            with open(DND_PREV, "w") as f:
                f.write(prev or "off")
            subprocess.run([OMARCHY_SHELL, "notifications", "setDnd", "on"], capture_output=True, timeout=3)
            hypr.request("eval hl.config({ misc = { enable_anr_dialog = false } })")
            hypr.request(f"eval {SPECIAL_ANIM_CONSOLE}")
            bar(hide=True)
            log(f"Hydra OS ativo: não perturbe ligado (antes: {prev}), sem barra e sem diálogo de travado")
        elif not active and os.path.exists(DND_PREV):
            with open(DND_PREV) as f:
                prev = f.read().strip() or "off"
            subprocess.run([OMARCHY_SHELL, "notifications", "setDnd", prev], capture_output=True, timeout=3)
            os.remove(DND_PREV)
            hypr.request("eval hl.config({ misc = { enable_anr_dialog = true } })")
            hypr.request(f"eval {SPECIAL_ANIM_DESK}")
            bar(hide=False)
            log(f"Hydra OS inativo: não perturbe volta a {prev}, barra e diálogo de travado de volta")
    except (OSError, subprocess.SubprocessError) as e:
        log(f"perfil do Hydra OS: {e!r}")


enter_pending_until = float("-inf")  # Guide com a TV fora da lista de monitores: repete quando ela aparecer


def enter():
    """Leva ao Hydra OS: liga a TV, foca a TV na área 4 e tira o cursor do caminho. False se a TV não está na lista
    de monitores (desligada ou em outra entrada): o resto das ações espera ela aparecer (monitoraddedv2)."""
    global bp_wanted_at, enter_pending_until
    spawn(f"{BIN}/tv-power", "on")  # já ligada: só confere o estado (rede, em segundo plano)
    tv = tv_monitor()
    if not tv:
        enter_pending_until = time.monotonic() + 30
        log("Hydra OS: TV fora da lista de monitores; ligando e esperando ela aparecer")
        return False
    bp_wanted_at = time.monotonic()
    # abertura: marca do Hydra por cima de tudo enquanto a TV vai para a área 4 por baixo; depois esmaece
    if backdrop_ensure(tv) and backdrop_send("splash on"):
        threading.Timer(SPLASH_SECONDS, act, (backdrop_send, "splash off")).start()
    hypr.dispatch(f'hl.dsp.focus({{ monitor = "{tv["name"]}" }})')
    hypr.dispatch(f'hl.dsp.focus({{ workspace = "{GAME_WS}" }})')
    park_cursor(tv)
    sync_tv()
    return True


def park_cursor(tv=None):
    """Focar monitor ou janela leva o cursor ao centro: com a TV na área 4 ele vai para o último pixel da TV
    (só a ponta da seta, que some em 2 s). Chamar DEPOIS do último foco."""
    tv = tv or tv_monitor()
    if tv and tv["activeWorkspace"]["name"] == GAME_WS:
        x = tv["x"] + round(tv["width"] / tv["scale"]) - 1
        y = tv["y"] + round(tv["height"] / tv["scale"]) - 1
        hypr.dispatch(f"hl.dsp.cursor.move({{ x = {x}, y = {y} }})")


def bp_overlay_shown(tv=None):
    tv = tv or tv_monitor()
    return bool(tv and tv["specialWorkspace"]["name"] == WARM_WS)


def toggle_bp_overlay():
    hypr.dispatch(f'hl.dsp.workspace.toggle_special("{WARM_WS.split(":", 1)[1]}")')


def show(addr):
    """Foca a janela em tela cheia no Hydra OS. O BP com um jogo em tela cheia na área 4 vai por CIMA dele, na área
    especial (WARM_WS) mostrada na TV, como o menu Home de um console: o jogo continua em tela cheia embaixo, sem
    encolher nem se minimizar (jogo em tela cheia exclusiva some ao perder a tela cheia). Sem jogo, o BP fica na 4."""
    cs = hypr.clients() or []
    c = next((c for c in cs if c["address"] == addr), None)
    if not c:
        return
    tv = tv_monitor()
    game_fs = hos.game and any(g["address"] == hos.game and g["workspace"]["name"] == GAME_WS and g.get("fullscreen")
                               for g in cs)
    if is_bp(c) and game_fs and tv:
        if c["workspace"]["name"] != WARM_WS:
            hypr.dispatch(f'hl.dsp.window.move({{ workspace = "{WARM_WS}", window = "address:{addr}", follow = false }})')
        if not bp_overlay_shown(tv):
            hypr.dispatch(f'hl.dsp.focus({{ monitor = "{tv["name"]}" }})')
            toggle_bp_overlay()
        focus(addr, fullscreen=True)
        park_cursor()
        return
    if bp_overlay_shown(tv) and c["workspace"]["name"] != WARM_WS:
        toggle_bp_overlay()  # esconde o BP de cima: o jogo já está em tela cheia embaixo
    if c["workspace"]["name"] != GAME_WS:
        hypr.dispatch(f'hl.dsp.window.move({{ workspace = "{GAME_WS}", window = "address:{addr}" }})')
    focus(addr, fullscreen=True)
    park_cursor()


def open_bp():
    """BP pedido pelo Hydra OS: foca o que existe ou pede ao Neo. Com a regra "4 silent" o BP novo nasce sem foco;
    quem foca é o openwindow do game_exit_watch. Só com a TV na área 4: não rouba a tela de quem está na mesa."""
    global bp_wanted_at
    tv = tv_monitor()
    if not (tv and tv["activeWorkspace"]["name"] == GAME_WS):
        log("Hydra OS: BP não reaberto (TV fora da área 4)")
        return
    bp_wanted_at = time.monotonic()
    if bp := bp_window(hypr.clients() or []):
        show(bp)
    elif not send_signal():
        log("Hydra OS: Hydra fora do ar -> abre direto no BP")
        launch_hydra("big-picture")


def run_hos(actions):
    global bd_text, bd_art, bd_hold, bp_wanted_at
    for kind, *args in actions:
        log(f"Hydra OS: {kind} {' '.join(map(str, args))}".rstrip())
        if kind == "enter" and not enter():
            return  # sem TV: nada de focar BP/jogo em outro monitor
        elif kind == "focus":
            show(args[0])
        elif kind == "open_bp":
            open_bp()
        elif kind == "restart_neo":
            bp_wanted_at = time.monotonic()
            launch_hydra("big-picture")
        elif kind == "leave":
            tv = tv_monitor()
            if tv and tv["activeWorkspace"]["name"] == GAME_WS:
                hypr.dispatch('hl.dsp.focus({ workspace = "3" })')
        elif kind == "text":
            bd_text = args[0]
            backdrop_send(f"text {bd_text}")
        elif kind == "art":
            bd_art = (args[0], args[1])
            backdrop_send("art " + "\t".join(bd_art))
        elif kind == "hold":
            bd_hold = args[0]
            sync_tv()


def hos_tick():
    run_hos(hos.tick(time.monotonic()))


def hos_focused(kind):
    hos.focused(kind)  # pelo nome global: o adopt_snapshot troca o objeto


def hos_later(delay):
    """O tick do HydraOS resolve prazos curtos (saída do jogo, BP fechado); o periodic cobre o do carregamento."""
    threading.Timer(delay + 0.1, act, (hos_tick,)).start()


def refresh_art():
    """Nome/fundo/logo dos jogos personalizados (cópia do banco do Neo, ~1 s) em segundo plano."""
    spawn("node", ART_TOOL, "art", neolog.ART_CACHE)


def hos_launched(game_id):
    name, hero, logo = neolog.game_info(game_id)
    if not game_id.startswith("steam:") and not name:
        refresh_art()  # jogo personalizado novo: desta vez sem arte, da próxima já com
    run_hos(hos.launched(name, time.monotonic(), game_id, hero, logo))
    sync_tv()  # tela de carregamento por cima do BP


def set_sub(text):
    global bd_sub
    if text != bd_sub:
        bd_sub = text
        backdrop_send(f"sub {text}")


def steam_launch_window(addr, opened):
    """Janela "Launching..." da Steam (escondida no special:steamlaunch): enquanto ela existe a Steam está preparando o
    jogo (shaders, nuvem, atualização); a tela de carregamento diz isso numa 2ª linha em vez de ficar muda."""
    global steam_launching
    if opened:
        steam_launching = addr
        set_sub("Preparando na Steam…" if hos.phase == "launching" else "")
    elif addr == steam_launching:
        steam_launching = None
        set_sub("")


def hos_opened_untracked(game_id):
    """Jogo que subiu sem "Launched": vira lançamento (tela de carregamento) se a janela dele ainda não abriu. Também
    completa o "Iniciando…" genérico da janela de lançamento do Neo com o nome e a arte."""
    if hos.game is None and (hos.phase != "launching" or hos.game_id is None):
        hos_launched(game_id)


def neo_launch_window(addr):
    """Janela sem título do Neo na área 4 com o BP aberto: a tela "Iniciando…" dele (capa num canto, texto solto: na
    TV parece quebrada). Vai para uma área escondida e a tela de carregamento do Hydra OS entra no lugar; nome e arte
    chegam com o log do Neo. Sem BP aberto é a janela principal (saída do BP): fica com o bp_closed_to_tray."""
    cs = hypr.clients() or []
    if not any(is_bp(c) for c in cs):
        return
    hypr.dispatch(f'hl.dsp.window.move({{ workspace = "special:hidden", window = "address:{addr}", follow = false }})')
    if hos.game is None and hos.phase != "launching":
        log("Hydra OS: o Neo começou a lançar um jogo -> tela de carregamento")
        run_hos(hos.launched(None, time.monotonic()))
    sync_tv()


def hos_process(running, game_id=None):
    run_hos(hos.process(running, time.monotonic(), game_id))


def hos_bp_closed():
    run_hos(hos.bp_closed(hydra_main() is not None, time.monotonic(), restarting=time.monotonic() < restarting_until))
    hos_later(hydraos.CLOSE_GRACE)


def hos_window_opened(addr):
    c = next((c for c in hypr.clients() or [] if c["address"] == addr), None)
    if c and c["workspace"]["name"] == GAME_WS and c.get("class") != "hydralauncher" and game_window(c):
        run_hos(hos.window(addr, "game", True, time.monotonic()))
        set_sub("")


def hos_window_closed(addr):
    if addr in dialogs:
        dialogs.discard(addr)
        run_hos(hos.window(addr, "dialog", False, time.monotonic()))
    elif addr == hos.game:
        # outra janela do jogo ainda na área 4 (fechou só um diálogo dele): ela vira o jogo, nada de BP
        other = next((c["address"] for c in hypr.clients() or [] if c["workspace"]["name"] == GAME_WS
                      and c["address"] != addr and c.get("class") != "hydralauncher" and game_window(c)), None)
        run_hos(hos.window(addr, "game", False, time.monotonic(), remaining=other))
        hos_later(hydraos.EXIT_GRACE)
        hos_later(hydraos.GONE_MAX)  # processo vivo sem janela: o limite de espera


def neo_log_watch():
    """Log do Neo: 'Launched' (o BP vai fechar; mostra 'Iniciando <jogo>…') e o ProcessWatcher (jogo abriu/fechou)."""
    f = neolog.Follower(neolog.INFO_LOG)
    launched_id = None
    refresh_art()
    while True:
        for line in f.read():
            if ev := neolog.parse(line):
                kind, game_id = ev
                log(f"Neo: {kind} {game_id}")
                if kind == "launched":
                    launched_id = game_id
                    act(hos_launched, game_id)
                elif kind == "opened" and game_id != launched_id:
                    # jogo personalizado (emulador, script): o Neo não loga "Launched", só o "opened" quando ele sobe
                    launched_id = game_id
                    act(hos_opened_untracked, game_id)
                elif kind == "closed" and game_id == launched_id:
                    launched_id = None  # o mesmo jogo aberto de novo é outro lançamento
                if kind != "launched":
                    act(hos_process, kind == "opened", game_id)
        time.sleep(0.25)


def tv_output_changed(added):
    """A TV saiu ou voltou da lista de monitores (desligou, trocou de entrada): a tela base presa à saída antiga
    morre sozinha (hydraos-backdrop sai no "invalidate"); reabre já, sem a trava de 10 s. Guide pendente: repete."""
    global bd_spawned, enter_pending_until
    bd_spawned = float("-inf")
    if added and time.monotonic() < enter_pending_until:
        enter_pending_until = float("-inf")
        log("Hydra OS: TV apareceu, repetindo o Guide")
        wake_open()
    sync_tv()


def adopt_snapshot():
    """Daemon (re)iniciado: um jogo em tela cheia na área 4 vira o jogo do Hydra OS."""
    global hos
    game = next((c["address"] for c in hypr.clients() or [] if c["workspace"]["name"] == GAME_WS
                 and c.get("fullscreen") and c.get("class") != "hydralauncher" and game_window(c)), None)
    hos = hydraos.HydraOS(game=game)
    if game:
        log(f"Hydra OS: jogo {game} já estava na área 4")
    sync_tv()


def game_exit_watch():
    """Jogo em tela cheia fechou com o BP vivo no mesmo workspace -> foco + tela cheia de volta
    ao BP. O Hyprland só deixa uma tela cheia por workspace, então o BP perde a dele quando o
    jogo entra. Vale para qualquer jogo (Steam, Eden, PCSX2, RPCS3, shadPS4)."""
    wins, last_active = {}, None
    act(adopt_snapshot)  # daemon reiniciado com o 4 já na TV (e talvez um jogo nele)
    for ev, data in hypr.events():
        if ev in ("fullscreen", "openwindow", "closewindow", "movewindowv2", "openlayer"):
            act(sync_tv)  # o que há na área 4 mudou (tela base "base" ou "cover"); camada nova pode ficar presa
        if ev in ("monitoraddedv2", "monitorremovedv2") and TV_DESC in data:
            act(tv_output_changed, ev == "monitoraddedv2")
        if ev == "closewindow":
            w = wins.pop("0x" + data, None)
            if w and is_bp(w) and w["workspace"]["name"] != WARM_WS:
                threading.Thread(target=bp_closed_to_tray, args=(w["pid"],), daemon=True).start()
                # o Neo fecha o BP ao lançar, ao "Sair do Big Picture" e ao cair: 0,4 s para saber se ele ainda vive
                threading.Timer(0.4, act, (hos_bp_closed,)).start()
                continue
            act(hos_window_closed, "0x" + data)  # jogo da área 4 fechou (volta ao BP) ou diálogo segurado sumiu
            if w and w.get("class") == "steam" and w.get("title") == "Launching...":
                act(steam_launch_window, "0x" + data, False)
        elif ev in ("workspacev2", "focusedmonv2", "monitoraddedv2", "monitorremovedv2"):
            act(sync_tv)
            continue
        elif ev in ("openwindow", "fullscreen", "movewindowv2") or (ev == "activewindowv2" and data != last_active):
            addr, _, cls = (data.split(",", 3) + ["", ""])[:3]
            parts = data.split(",")
            if (ev == "openwindow" and parts[1:2] == [GAME_WS]) or (ev == "movewindowv2" and parts[2:3] == [GAME_WS]):
                act(expel, "0x" + addr)
                act(hos_window_opened, "0x" + addr)  # janela de jogo na área 4: carregamento acabou
            if ev == "openwindow" and cls == "steam" and data.split(",", 3)[3:] == ["Launching..."]:
                act(steam_launch_window, "0x" + addr, True)
            if ev == "openwindow" and cls == "hydralauncher" and data.split(",", 3)[3:] == [""] and parts[1:2] == [GAME_WS]:
                act(neo_launch_window, "0x" + addr)
            if ev == "openwindow" and cls == "hydralauncher" and data.split(",", 3)[3:] == [BP_TITLE] and not warming:
                # BP novo nasce sem foco (regra "4 silent"): o Guide, o fim de jogo e o hydra-bp contam com ele focado
                log(f"BP aberto 0x{addr} -> foco na área {GAME_WS}")
                act(show, "0x" + addr)
            if ev == "activewindowv2" and (w := wins.get("0x" + data)) and w["workspace"]["name"] == GAME_WS:
                if is_bp(w):
                    act(hos_focused, "bp")
                elif w.get("class") != "hydralauncher" and game_window(w):
                    act(hos_focused, "game")
            if ev == "openwindow" and cls in FS_ON_OPEN:
                log(f"jogo {cls} abriu -> tira a tela cheia do BP e dá ao jogo")
                act(focus, "0x" + addr, True)
            # foto das janelas para saber, no closewindow, se a que fechou estava em tela cheia
            last_active = data if ev == "activewindowv2" else last_active
            wins = {c["address"]: c for c in hypr.clients() or []} or wins


def launch_hydra(mode):
    """Abre o Hydra fora do cgroup deste serviço: reiniciar o daemon mataria um filho direto."""
    subprocess.run(["systemd-run", "--user", "--collect", "--quiet", HYDRA, f"--{mode}"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# --- workarounds do Hydra 4.4 (bundle ofuscado, sem os patches do hydra-update): feitos por fora, pelo Hyprland ---
def bp_closed_to_tray(pid):
    """Saiu do BP: o Hydra mostra a janela principal. Fecha ela e o Hydra fica na bandeja (patch "BP→bandeja").
    Só janelas do mesmo processo: um Hydra reiniciado pelo daemon tem outro pid e o BP novo dele fica."""
    deadline = time.monotonic() + 2.5
    while time.monotonic() < deadline:
        main = next((c for c in hypr.clients() or [] if c.get("pid") == pid and not c.get("fullscreen")
                     and "hydra" in c.get("class", "").lower()), None)
        if main:
            log("saiu do BP -> janela principal para a bandeja")
            hypr.dispatch(f'hl.dsp.window.close({{ window = "address:{main["address"]}" }})')
            return
        time.sleep(0.05)


def warm_bp(pid):
    """Hydra novo na bandeja (--hidden): abre o BP já escondido em WARM_WS, e o 1º Guide só traz a janela
    (~20 ms em vez de ~0,8 s; patch "BP pronto no boot"). Custa ~250 MB e um piscar do BP ao subir."""
    global warming
    if time.monotonic() - bp_wanted_at < 30:
        return  # o Hydra subiu porque alguém pediu o BP (Guide, Neo reaberto): esse BP fica à vista
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            if b"--hidden" not in f.read().split(b"\0"):
                return
    except OSError:
        return
    deadline = time.monotonic() + 30  # a instância única só aceita depois do startup
    while time.monotonic() < deadline:
        cs = hypr.clients()
        if cs is None or any(c.get("pid") == pid for c in cs) or game_running() or time.monotonic() - bp_wanted_at < 30:
            return  # alguém já abriu uma janela do Hydra ou pediu o BP, ou tem jogo: não mexe
        warming = True
        if send_signal():
            break
        warming = False
        time.sleep(1)
    else:
        return
    try:
        while time.monotonic() < deadline:
            w = next((c for c in hypr.clients() or [] if c.get("pid") == pid), None)
            if w:
                hypr.dispatch(f'hl.dsp.window.move({{ workspace = "{WARM_WS}", window = "address:{w["address"]}", follow = false }})')
                log(f"BP aquecido e escondido {w['address']}")
                return
            time.sleep(0.02)
    finally:
        warming = False


def wake_open():
    """PC acordou pelo controle: Hydra OS no último estado (liga a TV), como o Guide de fora."""
    cs = hypr.clients() or []
    game = hos.game if any(c["address"] == hos.game for c in cs) else None
    run_hos(hos.guide(False, hypr.active(), bp_window(cs), game, time.monotonic()))


def suspend():
    time.sleep(0.5)  # deixa o aviso aparecer
    spawn(f"{BIN}/pc-suspend")  # desliga a TV antes (depois do suspend a rede já caiu)


def sleep_watch():
    """Suspender ou desligar por outro caminho (menu de energia do BP, que fala direto com o logind): a TV desliga
    antes, se estava no Hydra OS (área 4). Segura um inhibitor "delay" do logind (até 5 s) e ouve o PrepareFor*."""
    def inhibit():
        return subprocess.Popen(["systemd-inhibit", "--what=sleep:shutdown", "--mode=delay", "--who=Hydra OS",
                                 "--why=desligar a TV antes", "sleep", "infinity"],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    while True:
        lock = inhibit()
        mon = subprocess.Popen(["gdbus", "monitor", "--system", "--dest", "org.freedesktop.login1",
                                "--object-path", "/org/freedesktop/login1"],
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        for line in mon.stdout:
            if "PrepareForSleep (true,)" in line or "PrepareForShutdown (true,)" in line:
                tv = tv_monitor()
                if tv and tv["activeWorkspace"]["name"] == GAME_WS:
                    log("suspender/desligar com o Hydra OS na TV: TV desliga antes")
                    with open(LOG, "a") as err:
                        subprocess.run(["timeout", "3", f"{BIN}/tv-power", "off"], stdout=subprocess.DEVNULL, stderr=err)
                lock.kill()
                lock.wait()
            elif ("PrepareForSleep (false,)" in line or "PrepareForShutdown (false,)" in line) and lock.poll() is not None:
                lock = inhibit()  # acordou: segura de novo para a próxima
        lock.kill()  # gdbus caiu (dbus reiniciado): recomeça
        time.sleep(5)


# --- buttons.env: gesto do controle -> ação (parser em buttons.py) ---
CFG, warnings = buttons.load_config(f"{HOME}/.config/console/buttons.env")
for w in warnings:
    log(w)



def do(name):
    if name == "hydra":
        act(press)
    elif name == "suspend":
        notify("Suspendendo…", 1500)
        act(suspend)
    elif name == "tv_pc":
        notify("TV → tela do PC", 1500)
        spawn(f"{BIN}/tv-power", "pc")
    elif name == "mangohud":
        act(mangohud_toggle)
    elif name == "screenshot":
        spawn(f"{BIN}/controller-screenshot")
    elif name == "steam_bp":
        spawn("steam", "steam://open/bigpicture")
    elif name == "pharos_tv":
        act(pharos_tv)


PHAROS = f"{HOME}/.local/share/pharos-gaming/app/pharos-gaming"  # instalado pelo scripts/install.sh do Pharos Gaming


def pharos_pid():
    return next((pid for pid, argv in cmdlines() if argv[0] == PHAROS.encode() and b"--tv" in argv), None)


def pharos_tv():
    """Menu do Pharos, overlay por cima do jogo: o Pharos --tv fica aberto e alterna (aparece/some) com SIGUSR1. Ele
    aparece sem tirar o foco nem a tela cheia do jogo (regra de janela que ele mesmo aplica) e prende o controle
    enquanto aparece: aí o Guide + View nem chega aqui, é o Pharos que fecha. Sem Pharos aberto, abre um (já visível)
    fora do cgroup deste serviço."""
    pid = pharos_pid()
    if pid:
        log(f"menu do Pharos: alterna ({pid})")
        os.kill(pid, signal.SIGUSR1)
        return
    log("menu do Pharos: abre")
    subprocess.run(["systemd-run", "--user", "--collect", "--quiet", PHAROS, "--tv"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def run(action):
    kind, *args = action
    if kind == "notify":
        notify(*args)
        return
    if kind == "vol_repeat":
        if vol_q.empty():  # TV lenta: não acumula (o volume continuaria depois de soltar)
            vol_q.put(args[0])
        return
    if kind == "screen":
        log(f"botão -> tela {args[0]}")
        act(screen, args[0])
        return
    if kind == "vol":
        log(f"botão -> vol {args[0]}")
        vol_q.put(args[0])
        return
    name = CFG[buttons.GESTURES[kind]]
    log(f"botão -> {kind} = {name}")
    do(name)


# --- 8BitDo em modo Switch: Steam x Hydra brigando pelo hidraw ---
# O Chromium do Hydra Electron lia controle Nintendo só pelo hidraw (mantido no Neo até confirmar que ele não precisa) e não refaz o
# handshake num hotplug: o BP para de navegar até reiniciar o Hydra. E o SDL do Steam, ao
# soltar o controle, devolve ele ao modo HID simples (0x3f), que o hid-nintendo não entende ->
# nenhum evento evdev (nem o Home). Ver gotchas/hydra-bp-pro-controller-not-detected.md no ai-memory.
GAME_PATTERNS = (b"wineserver", b"SteamLaunch", b"umu-run", b"/bin/eden -f -g", b"legendary", b"gogdl", b"RetroArch-Linux-x86_64")
FULL_MODE = bytes([0x01, 0, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x03, 0x30])
restarting_until = float("-inf")
restart_pending = None  # None | "hidden" | "big-picture": reinício adiado até o jogo fechar


def cmdlines():
    for p in os.listdir("/proc"):
        if p.isdigit():
            try:
                with open(f"/proc/{p}/cmdline", "rb") as f:
                    yield int(p), f.read().split(b"\0")
            except OSError:
                pass


def hydra_main():
    for pid, argv in cmdlines():
        if argv[0] == HYDRA_BIN.encode() and not any(a.startswith(b"--type=") for a in argv):
            return pid
    return None


def game_running():
    return any(any(g in b" ".join(argv) for g in GAME_PATTERNS) for _, argv in cmdlines())


def pro_hidraw(sysfs):
    """/devices/virtual/misc/uhid/0005:057E:2009.NNNN/input/inputN -> /dev/hidrawM"""
    try:
        return "/dev/" + os.listdir(f"/sys{os.path.dirname(os.path.dirname(sysfs))}/hidraw")[0]
    except (OSError, IndexError):
        return None


def ensure_full_mode(dev):
    """Modo 0x3f (simples) -> manda o subcomando 0x03/0x30. Lê o hidraw com até 0,5s de espera."""
    try:
        fd = os.open(dev, os.O_RDWR | os.O_NONBLOCK)
    except OSError:
        return
    try:
        if select.select([fd], [], [], 0.5)[0] and os.read(fd, 64)[:1] == b"\x3f":
            os.write(fd, FULL_MODE)
            log(f"{dev} em 0x3f -> forçado 0x30")
    except OSError:
        pass
    finally:
        os.close(fd)


def restart_hydra(mode):
    """Fecha o Hydra (SIGTERM no processo principal: saída limpa) e reabre."""
    global bp_addr
    pid = hydra_main()
    if not pid:
        return
    global restarting_until, bp_wanted_at
    log(f"reiniciando Hydra ({mode})")
    restarting_until = time.monotonic() + 15  # o BP que fecha agora não é "Sair do BP" nem queda (hos_bp_closed)
    if mode == "big-picture":
        bp_wanted_at = time.monotonic()  # o warm_bp não esconde o BP que volta
    os.kill(pid, 15)
    for _ in range(100):
        if not os.path.exists(f"/proc/{pid}"):
            break
        time.sleep(0.1)
    else:
        os.kill(pid, 9)
        time.sleep(1)
    bp_addr = None
    launch_hydra(mode)


def pro_connected(sysfs, wake):
    """8BitDo (re)conectou: espera o Steam/kernel terminarem o handshake, garante o modo
    0x30 e reinicia o Hydra para ele reenumerar o controle."""
    global restart_pending
    time.sleep(4)
    if dev := pro_hidraw(sysfs):
        ensure_full_mode(dev)
    if wake:
        # PC acordado pelo controle: sempre o Hydra OS no último estado (o BP ou o jogo que ficou aberto), mesmo
        # quando o reinício do Hydra abaixo é adiado porque há jogo rodando (antes a TV ligava na área 3)
        act(wake_open)
    if not hydra_main():
        return
    mode = "big-picture" if wake or any(is_bp(c) and c["workspace"]["name"] != WARM_WS for c in hypr.clients() or []) else "hidden"
    if game_running():
        restart_pending = mode
        log("jogo rodando: reinício do Hydra adiado")
        return
    restart_hydra(mode)


# --- controles: descoberta, hotplug e leitura ---
def controller_nodes():
    """{(event node, sysfs path): nome} dos controles conectados agora. O sysfs
    (.../input/inputN) muda a cada reconexão mesmo quando o eventN é reciclado: é a
    identidade que denuncia um fd obsoleto (que nunca dá EOF)."""
    found = {}
    try:
        with open("/proc/bus/input/devices") as f:
            blocks = f.read().split("\n\n")
    except OSError:
        return found
    for block in blocks:
        name = sysfs = node = None
        for line in block.splitlines():
            if line.startswith("N: Name="):
                name = line[8:].strip('"')
            elif line.startswith("S: Sysfs="):
                sysfs = line[9:]
            elif line.startswith("H: Handlers="):
                node = next(("/dev/input/" + t for t in line[12:].split() if t.startswith("event")), None)
        if name in NAMES and node and sysfs:
            found[(node, sysfs)] = name
    return found


class Dev:
    def __init__(self, key, name, shared):
        self.key, self.name = key, name
        self.fd = os.open(key[0], os.O_RDONLY | os.O_NONBLOCK)
        self.pad = buttons.Pad(name, shared)

    def fileno(self):
        return self.fd

    def close(self):
        try:
            os.close(self.fd)
        except OSError:
            pass


def hotplug_socket():
    """Eventos do udev (grupo 2 do netlink, já com as permissões do device aplicadas)."""
    nl = socket.socket(socket.AF_NETLINK, socket.SOCK_DGRAM | socket.SOCK_NONBLOCK, 15)
    nl.bind((0, 2))
    return nl


def slept_total():
    """Tempo total suspenso desde o boot (BOOTTIME conta a suspensão, MONOTONIC não)."""
    return time.clock_gettime(time.CLOCK_BOOTTIME) - time.clock_gettime(time.CLOCK_MONOTONIC)


WAKE_WINDOW = 90  # 8BitDo leva ~60s pra reconectar após o resume (handle BT velho); controle que conecta até 90s após acordar = PC ligado pelo controle


class Daemon:
    def __init__(self):
        self.shared = buttons.shared_from(CFG)
        self.devs = {}  # fd -> Dev
        self.first_scan = True  # controles já conectados quando o daemon sobe não reiniciam o Hydra
        self.slept_ref, self.resume_at = slept_total(), None
        self.last_mode_check = float("-inf")
        self.denied = set()  # controles que não abriram (loga uma vez só)
        self.hydra_pid = None

    def check_resume(self):
        s = slept_total()
        if s - self.slept_ref > 2:
            self.resume_at = time.monotonic()
            log(f"resume (suspenso {s - self.slept_ref:.0f}s)")
        self.slept_ref = s

    def drop(self, dev):
        dev.close()
        del self.devs[dev.fd]

    def rescan(self, hotplug=False):
        """Abre controles novos e fecha os que sumiram ou cujo node foi reciclado."""
        self.check_resume()  # antes de abrir: o controle que acorda o PC conecta logo após o resume
        now = controller_nodes()
        for dev in list(self.devs.values()):
            if dev.key not in now:
                self.drop(dev)
        opened = {d.key for d in self.devs.values()}
        for key, name in now.items():
            if key in opened:
                continue
            try:
                dev = Dev(key, name, self.shared)
            except OSError as e:
                # no hotplug o 1º evento chega antes da permissão do udev e o seguinte abre:
                # só a checagem periódica avisa (aí a falha é de verdade)
                if not hotplug and key not in self.denied:
                    self.denied.add(key)
                    log(f"não abriu {name} ({key[0]}): {e!r}")
                continue
            self.devs[dev.fd] = dev
            log(f"controle conectado: {name}")
            # acordar pelo teclado com o controle desligado não abre nada:
            # só um controle conectando logo após o resume dispara o BP
            wake = self.resume_at is not None and time.monotonic() - self.resume_at < WAKE_WINDOW
            if wake:
                self.resume_at = None
            if name == buttons.PRO and not self.first_scan:
                # o restart do Hydra (4s depois) mataria um BP aberto agora: ele reabre no BP
                threading.Thread(target=pro_connected, args=(key[1], wake), daemon=True).start()
            elif wake:
                act(wake_open)
        self.first_scan = False

    def periodic(self):
        global restart_pending
        self.rescan()
        if hos.phase == "launching":
            if not hos.proc and game_running():
                act(hos_process, True)  # Proton/umu já subiu (prefixo novo demora antes do exe do jogo existir)
            act(hos_tick)  # prazo do carregamento (sem o processo do jogo em 90 s: volta ao BP)
        pid = hydra_main()
        if pid != self.hydra_pid:
            self.hydra_pid = pid
            if pid:
                threading.Thread(target=warm_bp, args=(pid,), daemon=True).start()
        # Steam fechando devolve o 8BitDo pro modo 0x3f sem reconectar: confere a cada ~10s
        if time.monotonic() - self.last_mode_check > 10:
            self.last_mode_check = time.monotonic()
            for dev in self.devs.values():
                if dev.name == buttons.PRO and (hidraw := pro_hidraw(dev.key[1])):
                    act(ensure_full_mode, hidraw)
            if restart_pending and not game_running():
                mode, restart_pending = restart_pending, None
                threading.Thread(target=restart_hydra, args=(mode,), daemon=True).start()

    def read(self, dev):
        try:
            data = os.read(dev.fd, 24 * 64)
            if not data:
                raise OSError("eof")
        except BlockingIOError:
            return
        except OSError:  # controle desconectou
            self.drop(dev)
            return
        now = time.monotonic()
        for i in range(0, len(data) - 23, 24):
            t, c, v = struct.unpack_from("HHi", data, i + 16)
            if t == 1:  # EV_KEY
                for a in dev.pad.key(c, v, now):
                    run(a)
            elif t == 3:  # EV_ABS: só o D-pad interessa (pad.hat ignora o resto)
                for a in dev.pad.hat(c, v, now):
                    run(a)

    def loop(self):
        nl = hotplug_socket()
        self.periodic()
        next_tick = time.monotonic() + TICK
        while True:
            # botão segurado -> acorda a cada 30 ms pra medir o tempo sem esperar evento
            busy = any(d.pad.busy for d in self.devs.values())
            wait = 0.03 if busy else max(0, next_tick - time.monotonic())
            r, _, _ = select.select([nl, *self.devs.values()], [], [], wait)
            if nl in r:
                plugged = False
                try:
                    while True:
                        plugged |= b"SUBSYSTEM=input" in nl.recv(65536)
                except BlockingIOError:
                    pass
                except OSError:  # ENOBUFS: perdeu eventos na rajada -> rescan resolve
                    plugged = True
                if plugged:
                    self.rescan(hotplug=True)
            for dev in r:
                if dev is not nl and dev.fd in self.devs:
                    self.read(dev)
            now = time.monotonic()
            for dev in list(self.devs.values()):
                for a in dev.pad.tick(now):
                    run(a)
            if now >= next_tick:
                next_tick = now + TICK
                self.periodic()


if __name__ == "__main__":
    for target in (vol_worker, action_worker, game_exit_watch, neo_log_watch, sleep_watch):
        threading.Thread(target=target, daemon=True).start()
    log("daemon iniciado")
    Daemon().loop()
