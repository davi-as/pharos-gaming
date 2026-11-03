"""Hyprland pelo socket de controle, sem hyprctl: ~0,07 ms por consulta contra ~3,5 ms do subprocess."""
import json, os, socket, time

BASE = f"/run/user/{os.getuid()}/hypr"


def _instances():
    """Instância do env primeiro, depois as outras da mais nova para a mais velha. O serviço
    systemd sobe sem HYPRLAND_INSTANCE_SIGNATURE e o Hyprland ganha outra a cada reinício."""
    try:
        sigs = sorted(os.listdir(BASE), key=lambda s: os.path.getmtime(f"{BASE}/{s}"), reverse=True)
    except OSError:
        sigs = []
    cur = os.environ.get("HYPRLAND_INSTANCE_SIGNATURE")
    return ([cur] if cur else []) + [s for s in sigs if s != cur]


def request(cmd):
    """Resposta como texto, ou None se o Hyprland não responder."""
    for sig in _instances():
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                s.settimeout(2)
                s.connect(f"{BASE}/{sig}/.socket.sock")
                s.sendall(cmd.encode())
                out = b""
                while chunk := s.recv(65536):
                    out += chunk
        except OSError:
            continue
        os.environ["HYPRLAND_INSTANCE_SIGNATURE"] = sig  # filhos (hyprctl, grim) herdam a certa
        return out.decode(errors="replace")
    return None


def _json(cmd):
    try:
        return json.loads(request(cmd) or "")
    except ValueError:
        return None


def clients():
    """Lista de janelas, ou None se o Hyprland não respondeu (None = não decidir nada)."""
    return _json("j/clients")


def active_window():
    return _json("j/activewindow") or {}


def active():
    return active_window().get("address")


def dispatch(arg):
    request(f"dispatch {arg}")


def events():
    """Eventos do Hyprland (socket2) como (nome, dados), para sempre: reconecta se o
    Hyprland reiniciar. Endereços vêm sem o 0x."""
    while True:
        for sig in _instances():
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                    s.connect(f"{BASE}/{sig}/.socket2.sock")
                    buf = b""
                    while chunk := s.recv(65536):
                        *lines, buf = (buf + chunk).split(b"\n")
                        for line in lines:
                            ev, _, data = line.decode(errors="replace").partition(">>")
                            yield ev, data
            except OSError:
                continue
        time.sleep(2)
