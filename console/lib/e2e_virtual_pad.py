#!/usr/bin/env python3
"""Teste de ponta a ponta do daemon: controle virtual (uinput) -> evdev -> buttons.py -> buttons.env -> ação.

    python3 ~/.local/lib/console/e2e_virtual_pad.py

Grava um buttons.env de teste (Share = none, View + Start = none: nada acontece de verdade), reinicia o serviço,
cria um "Xbox Wireless Controller" virtual, aperta Share e View + Start e confere no log do daemon que cada gesto
virou a ação configurada. No fim devolve o buttons.env que existia e reinicia o serviço de novo.
"""
import fcntl, os, shutil, struct, subprocess, sys, time

HOME = os.path.expanduser("~")
CFG = f"{HOME}/.config/console/buttons.env"
LOG = f"{HOME}/.local/state/xbox-guide/guide-press.log"
UI_SET_EVBIT, UI_SET_KEYBIT, UI_DEV_CREATE, UI_DEV_DESTROY = 0x40045564, 0x40045565, 0x5501, 0x5502
EV_SYN, EV_KEY = 0, 1
SHARE, SELECT, START, SOUTH = 167, 314, 315, 304  # BTN_SOUTH: sem ele o udev não marca joystick (nem dá a ACL)


def restart():
    subprocess.run(["systemctl", "--user", "restart", "xbox-guide"], check=True)
    time.sleep(1.5)


def emit(fd, code, value):
    os.write(fd, struct.pack("qqHHi", 0, 0, EV_KEY, code, value))
    os.write(fd, struct.pack("qqHHi", 0, 0, EV_SYN, 0, 0))


def main():
    backup = CFG + ".e2e-bak"
    had = os.path.exists(CFG)
    if had:
        shutil.copy(CFG, backup)
    try:
        with open(CFG, "w") as f:
            f.write("SHARE=none\nVIEW_START=none\n")
        restart()
        start = os.path.getsize(LOG)
        fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
        # uinput moderno: a struct uinput_user_dev vai ANTES dos ioctls (senão "write device info first")
        os.write(fd, struct.pack("80sHHHHi1024x", b"Xbox Wireless Controller", 0x05, 0x045E, 0x0B13, 1, 0))
        fcntl.ioctl(fd, UI_SET_EVBIT, EV_KEY)
        for k in (SHARE, SELECT, START, SOUTH):
            fcntl.ioctl(fd, UI_SET_KEYBIT, k)
        fcntl.ioctl(fd, UI_DEV_CREATE)
        try:
            time.sleep(4.5)  # o 1º evento do udev chega antes da ACL; o rescan de 3 s do daemon abre o controle
            emit(fd, SHARE, 1); time.sleep(0.05); emit(fd, SHARE, 0)
            time.sleep(0.3)
            emit(fd, SELECT, 1); time.sleep(0.05); emit(fd, START, 1); time.sleep(0.05)
            emit(fd, START, 0); emit(fd, SELECT, 0)
            time.sleep(0.8)
        finally:
            fcntl.ioctl(fd, UI_DEV_DESTROY)
            os.close(fd)
        with open(LOG) as f:
            f.seek(start)
            got = f.read()
        ok = "controle conectado: Xbox Wireless Controller" in got and "botão -> screenshot = none" in got and "botão -> chord = none" in got
        print(got.strip())
        print("OK" if ok else "FALHOU")
        return 0 if ok else 1
    finally:
        if had:
            shutil.move(backup, CFG)
        else:
            os.remove(CFG)
        restart()


if __name__ == "__main__":
    sys.exit(main())
