#!/usr/bin/env python3
"""console daemon: controle Xbox / 8BitDo -> Hydra Big Picture, TV, MangoHud, suspender.

Reage a eventos: o hotplug vem do netlink do udev (controle novo abre na hora), os botões
vêm do evdev. A lógica dos botões fica em buttons.py (com testes); as ações rodam numa
thread separada, então a leitura dos controles nunca trava.
Valores da máquina (MACs, IP da TV) ficam em ~/.config/console/config.env; o que cada gesto do
controle faz fica em ~/.config/console/buttons.env (editado pelo PHAROS GAMING, que reinicia o serviço).
"""
import fcntl, importlib.machinery, importlib.util, os, queue, select, signal, socket, struct, subprocess, threading, time

import buttons, hypr

NAMES = ("Xbox Wireless Controller", buttons.PRO)  # IMU do 8BitDo ("... (IMU)") fica de fora pelo match exato
HOME = os.path.expanduser("~")
BIN = f"{HOME}/.local/bin"
HYDRA = f"{BIN}/hydra"
HYDRA_BIN = f"{HOME}/.local/opt/hydra-patched/hydralauncher"
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
# Protocolo do singleton do Chromium (patch do Electron): START\0<cwd>\0<nargs>\0<argv...>
SOCK = f"{HOME}/.config/hydralauncher/SingletonSocket"
SIGNAL = b"\0".join([b"START", HOME.encode(), b"2", HYDRA.encode(), b"--big-picture"])

# Janela do BP rastreada pelo ENDEREÇO, não pelo fullscreen: o Hyprland só deixa uma janela
# fullscreen por workspace, então o BP perde o fullscreen quando o jogo ganha foco e continua vivo.
bp_addr = None
game_addr, game_fs = None, False  # janela de antes do BP, e se estava em fullscreen


def bp_window(cs):
    """Endereço do BP vivo (fullscreen ou não), adotando um BP fullscreen novo."""
    global bp_addr
    if not any(c["address"] == bp_addr for c in cs):
        bp_addr = next((c["address"] for c in cs
                        if "hydra" in c.get("class", "").lower() and c.get("fullscreen")), None)
    return bp_addr


def focus(addr, fullscreen):
    """Foca e deixa a janela com tela cheia ou sem, explicitamente: o Hyprland passa a tela
    cheia do BP para a janela que ganha foco, e uma janela normal viraria tela cheia."""
    hypr.dispatch(f'hl.dsp.focus({{window="address:{addr}"}})')
    action = "set" if fullscreen else "unset"  # sem action o dispatch alterna
    hypr.dispatch(f'hl.dsp.window.fullscreen({{ mode = "fullscreen", action = "{action}" }})')


def focused_monitor():
    return next((m for m in hypr._json("j/monitors") or [] if m["focused"]), None)


def bring_here(addr, cs):
    """BP vivo em outro monitor -> vem para o monitor em foco (escolhido com Guide + D-pad)."""
    mon, win = focused_monitor(), next((c for c in cs if c["address"] == addr), {})
    if mon and win.get("monitor") != mon["id"]:
        ws = mon["activeWorkspace"]["name"]
        log(f"press: BP vem para o workspace {ws} ({mon['name']})")
        hypr.dispatch(f'hl.dsp.window.move({{ workspace = "{ws}", window = "address:{addr}" }})')


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
    """Manda --big-picture para o Hydra que já está rodando. True se ele respondeu ACK."""
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
            s.settimeout(1)
            s.connect(os.readlink(SOCK))
            s.sendall(SIGNAL)
            s.shutdown(socket.SHUT_WR)
            return s.recv(8) == b"ACK"
    except OSError:
        return False


def press():
    """Guide: BP em foco -> volta ao jogo; BP vivo -> foca; senão abre (Hydra quente ou frio)."""
    global game_addr, game_fs, bp_addr
    cs, cur = hypr.clients() or [], hypr.active()
    addr = bp_window(cs)
    fs = {c["address"]: bool(c.get("fullscreen")) for c in cs}
    if addr and cur == addr:
        log(f"press: BP em foco -> volta para {game_addr} (fullscreen={game_fs})")
        if game_addr in fs and game_addr != addr:
            focus(game_addr, fullscreen=game_fs)
        return
    game_addr, game_fs = cur, fs.get(cur, False)
    if addr:
        log(f"press: foca o BP {addr}")
        bring_here(addr, cs)
        focus(addr, fullscreen=True)
        return
    if not send_signal():
        log("press: Hydra fora do ar -> abre direto no BP")
        launch_hydra("big-picture")
        return
    # O BP pronto (patch do hydra-update) ganha foco em ~20 ms. A tela cheia vem daqui: o
    # Electron perde o setFullScreen() de uma janela que nunca tinha sido mostrada.
    deadline = time.monotonic() + 4
    while time.monotonic() < deadline:
        w = hypr.active_window()
        if "hydra" in w.get("class", "").lower() and w.get("address") != cur:
            bp_addr = w["address"]
            log(f"press: BP aberto {bp_addr}")
            focus(bp_addr, fullscreen=True)
            return
        time.sleep(0.005)
    log("press: BP não apareceu em 4s")


# Jogos cuja regra fullscreen do Hyprland perde para o BP já em tela cheia no workspace da TV
# (uma tela cheia por workspace; o suppress_event impede o jogo de pedir depois).
FS_ON_OPEN = {"eldenring.exe"}


def game_exit_watch():
    """Jogo em tela cheia fechou com o BP vivo no mesmo workspace -> foco + tela cheia de volta
    ao BP. O Hyprland só deixa uma tela cheia por workspace, então o BP perde a dele quando o
    jogo entra. Vale para qualquer jogo (Steam, Eden, PCSX2, RPCS3, shadPS4)."""
    wins, last_active = {}, None
    for ev, data in hypr.events():
        if ev == "closewindow":
            w = wins.pop("0x" + data, None)
            if not (w and w.get("fullscreen") and w["address"] != bp_addr):
                continue
            bp = next((c for c in hypr.clients() or [] if c["address"] == bp_addr), None)
            if bp and bp["workspace"]["id"] == w["workspace"]["id"]:
                log(f"jogo {w.get('class')} fechou -> tela cheia de volta ao BP")
                act(focus, bp_addr, True)
        elif ev in ("openwindow", "fullscreen", "movewindowv2") or (ev == "activewindowv2" and data != last_active):
            addr, _, cls = (data.split(",", 3) + ["", ""])[:3]
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


def wake_open():
    """PC acordou pelo controle: liga a TV e abre/foca o BP (sem voltar ao jogo se já estiver nele)."""
    spawn(f"{BIN}/tv-power", "on")
    if not (bp_window(hypr.clients() or []) and hypr.active() == bp_addr):
        press()


def suspend():
    time.sleep(0.5)  # deixa o aviso aparecer
    spawn(f"{BIN}/pc-suspend")  # desliga a TV antes (depois do suspend a rede já caiu)


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
# O Chromium do Hydra lê controle Nintendo só pelo hidraw (handshake próprio) e não refaz o
# handshake num hotplug: o BP para de navegar até reiniciar o Hydra. E o SDL do Steam, ao
# soltar o controle, devolve ele ao modo HID simples (0x3f), que o hid-nintendo não entende ->
# nenhum evento evdev (nem o Home). Ver gotchas/hydra-bp-pro-controller-not-detected.md no ai-memory.
GAME_PATTERNS = (b"wineserver", b"SteamLaunch", b"umu-run", b"/bin/eden -f -g", b"legendary", b"gogdl", b"RetroArch-Linux-x86_64")
FULL_MODE = bytes([0x01, 0, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x03, 0x30])
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
    """Fecha o Hydra (SIGTERM só no processo principal: saída limpa do Electron) e reabre."""
    global bp_addr
    pid = hydra_main()
    if not pid:
        return
    log(f"reiniciando Hydra ({mode})")
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
    if not hydra_main():
        if wake:
            wake_open()
        return
    mode = "big-picture" if wake or bp_window(hypr.clients() or []) else "hidden"
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


WAKE_WINDOW = 30  # controle que conecta até 30s após acordar = PC ligado pelo controle


class Daemon:
    def __init__(self):
        self.shared = buttons.shared_from(CFG)
        self.devs = {}  # fd -> Dev
        self.first_scan = True  # controles já conectados quando o daemon sobe não reiniciam o Hydra
        self.slept_ref, self.resume_at = slept_total(), None
        self.last_mode_check = float("-inf")
        self.denied = set()  # controles que não abriram (loga uma vez só)

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
    for target in (vol_worker, action_worker, game_exit_watch):
        threading.Thread(target=target, daemon=True).start()
    log("daemon iniciado")
    Daemon().loop()
