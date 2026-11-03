#!/usr/bin/env python3
# Testes da lógica dos botões: python3 ~/.local/lib/console/test_buttons.py
import unittest

import os, tempfile

from buttons import CAPTURE_ALT, GUIDE, HAT0X, LB, PRO, RB, SELECT, SHARE, START, Pad, Shared, load_config, shared_from

XBOX = "Xbox Wireless Controller"


def kinds(actions):
    return [a[0] for a in actions]


class Sim:
    """Controle simulado: aperta/solta botões num relógio fictício, coletando as ações."""

    def __init__(self, name=XBOX, shared=None):
        self.pad, self.t, self.out = Pad(name, shared or Shared()), 100.0, []

    def ev(self, c, v, dt=0.0):
        self.wait(dt)
        self.out += self.pad.key(c, v, self.t)

    def wait(self, dt):
        end = self.t + dt
        while self.t < end:  # tick a cada 30 ms, como o daemon com um botão segurado
            self.t = min(end, self.t + 0.03)
            self.out += self.pad.tick(self.t)

    def tap(self, c, hold=0.1, dt=0.0):
        self.ev(c, 1, dt)
        self.ev(c, 0, hold)


class Screen(unittest.TestCase):
    def hat(self, s, v, dt=0.0):
        s.wait(dt)
        s.out += s.pad.hat(HAT0X, v, s.t)

    def test_guide_dpad_switches_screen_and_repeats_without_bp(self):
        s = Sim()
        s.ev(GUIDE, 1)
        self.hat(s, 1, 0.1); self.hat(s, 0, 0.05)
        self.hat(s, -1, 0.1); self.hat(s, 0, 0.05)
        s.ev(GUIDE, 0, 0.1)
        self.assertEqual(s.out, [("screen", "r"), ("screen", "l")])

    def test_dpad_alone_and_other_axes_do_nothing(self):
        s = Sim()
        self.hat(s, 1)
        s.ev(GUIDE, 1)
        s.out += s.pad.hat(0, 30000, s.t)  # analógico esquerdo
        s.ev(GUIDE, 0, 0.1)
        self.assertEqual(kinds(s.out), ["bp"])


class Guide(unittest.TestCase):
    def test_tap_opens_bp_on_release(self):
        s = Sim()
        s.ev(GUIDE, 1)
        self.assertEqual(s.out, [])
        s.ev(GUIDE, 0, 0.15)
        self.assertEqual(kinds(s.out), ["bp"])
        self.assertFalse(s.pad.busy)

    def test_hold_warns_then_release_cancels(self):
        s = Sim()
        s.tap(GUIDE, hold=1.2)
        self.assertEqual(kinds(s.out), ["notify", "notify"])
        self.assertIn("Continue", s.out[0][1])
        self.assertIn("cancelada", s.out[1][1])

    def test_hold_2s_suspends_once(self):
        s = Sim()
        s.tap(GUIDE, hold=3.0)
        self.assertEqual(kinds(s.out), ["notify", "suspend"])

    def test_hold_seconds_and_text_come_from_config(self):
        s = Sim(shared=Shared(hold=1.0, hold_text="abrir a Steam"))
        s.tap(GUIDE, hold=1.5)
        self.assertEqual(kinds(s.out), ["notify", "suspend"])
        self.assertIn("abrir a Steam", s.out[0][1])

    def test_hold_disabled_does_nothing(self):
        s = Sim(shared=Shared(hold=None))
        s.tap(GUIDE, hold=3.0)
        self.assertEqual(s.out, [])

    def test_debounce_shared_between_pads(self):
        shared = Shared()
        a, b = Sim(shared=shared), Sim(PRO, shared)
        a.tap(GUIDE)
        b.t = a.t + 0.5
        b.tap(GUIDE)
        self.assertEqual(kinds(a.out + b.out), ["bp"])
        b.tap(GUIDE, dt=1.0)
        self.assertEqual(kinds(b.out), ["bp"])


class Combos(unittest.TestCase):
    def test_guide_start_switches_tv_without_bp(self):
        s = Sim()
        s.ev(GUIDE, 1)
        s.tap(START, dt=0.1)
        s.ev(GUIDE, 0, 0.1)
        self.assertEqual(kinds(s.out), ["tv_pc"])

    def test_guide_rb_volume_repeats_and_rb_again_without_releasing_guide(self):
        s = Sim()
        s.ev(GUIDE, 1)
        s.ev(RB, 1, 0.1)
        self.assertEqual(s.out, [("vol", "KEY_VOLUP")])
        s.ev(RB, 0, 0.6)  # segurou 0,6s: 0,4 de espera e depois repete a cada 0,12
        self.assertEqual(kinds(s.out), ["vol", "vol_repeat", "vol_repeat"])
        s.out.clear()
        s.tap(LB, dt=0.2)
        s.ev(GUIDE, 0, 0.1)
        self.assertEqual(s.out, [("vol", "KEY_VOLDOWN")])  # e soltar o Guide não abre o BP

    def test_guide_rb_after_warning_cancels_suspend(self):
        s = Sim()
        s.ev(GUIDE, 1)
        s.ev(RB, 1, 1.0)
        s.wait(2.0)
        self.assertEqual(kinds(s.out[:3]), ["notify", "notify", "vol"])
        self.assertIn("cancelada", s.out[1][1])
        self.assertNotIn("suspend", kinds(s.out))

    def test_guide_view_opens_pharos_without_bp(self):
        s = Sim()
        s.ev(GUIDE, 1)
        s.tap(SELECT, dt=0.1)
        s.ev(GUIDE, 0, 0.1)
        self.assertEqual(kinds(s.out), ["pharos"])

    def test_volume_off_ignores_guide_rb(self):
        s = Sim(shared=Shared(volume=False))
        s.ev(GUIDE, 1)
        s.ev(RB, 1, 0.1)
        s.ev(RB, 0, 0.1)
        self.assertEqual(s.out, [])

    def test_rb_without_guide_does_nothing(self):
        s = Sim()
        s.tap(RB)
        self.assertEqual(s.out, [])

    def test_view_start_chord(self):
        s = Sim()
        s.ev(SELECT, 1)
        s.tap(START, dt=0.05)
        self.assertEqual(kinds(s.out), ["chord"])

    def test_start_then_view_quickly_is_chord(self):
        s = Sim()
        s.ev(START, 1)
        s.ev(SELECT, 1, 0.05)
        self.assertEqual(kinds(s.out), ["chord"])

    def test_start_then_view_late_is_nothing(self):
        s = Sim()
        s.ev(START, 1)
        s.ev(SELECT, 1, 0.3)
        self.assertEqual(s.out, [])


class Capture(unittest.TestCase):
    def test_share_debounced(self):
        s = Sim()
        s.tap(SHARE)
        s.tap(SHARE, dt=0.3)
        s.tap(SHARE, dt=1.0)
        self.assertEqual(kinds(s.out), ["screenshot", "screenshot"])

    def test_btn_z_only_on_pro(self):
        x, p = Sim(), Sim(PRO)
        x.tap(CAPTURE_ALT)
        p.tap(CAPTURE_ALT)
        self.assertEqual((x.out, kinds(p.out)), ([], ["screenshot"]))


class Config(unittest.TestCase):
    def load(self, text):
        with tempfile.NamedTemporaryFile("w", suffix=".env", delete=False) as f:
            f.write(text)
        try:
            return load_config(f.name)
        finally:
            os.unlink(f.name)

    def test_missing_file_is_defaults(self):
        cfg, warn = load_config("/nao/existe")
        self.assertEqual((cfg["GUIDE_TAP"], warn), ("hydra", []))

    def test_reads_values_and_rejects_unknown_action(self):
        cfg, warn = self.load("# comentário\nGUIDE_TAP=steam_bp\nGUIDE_HOLD=none\nSHARE=formatar\nHOLD_SECONDS=9\nGUIDE_VOLUME=0\n")
        self.assertEqual((cfg["GUIDE_TAP"], cfg["SHARE"]), ("steam_bp", "screenshot"))
        self.assertEqual(len(warn), 1)
        sh = shared_from(cfg)
        self.assertEqual((sh.hold, sh.volume), (None, False))
        self.assertEqual(shared_from(self.load("HOLD_SECONDS=9\n")[0]).hold, 3.0)


if __name__ == "__main__":
    unittest.main()
