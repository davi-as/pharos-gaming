#!/usr/bin/env python3
# Testes da máquina de estados do Hydra OS (área 4 da TV): python3 ~/.local/lib/console/test_hydraos.py
import unittest

from hydraos import HydraOS

G, BP = "0xg", "0xbp"


class GuideTest(unittest.TestCase):
    def test_de_fora_volta_ao_jogo(self):
        h = HydraOS(); h.window(G, "game", True, 0); h.focused("game")
        self.assertEqual(h.guide(False, None, BP, G, 1), [("enter",), ("focus", G)])

    def test_de_fora_ultimo_foi_o_bp(self):
        h = HydraOS(); h.window(G, "game", True, 0); h.focused("bp")
        self.assertEqual(h.guide(False, None, BP, G, 1), [("enter",), ("focus", BP)])

    def test_de_fora_sem_jogo_foca_bp(self):
        self.assertEqual(HydraOS().guide(False, None, BP, None, 0), [("enter",), ("focus", BP)])

    def test_de_fora_sem_nada_abre_bp(self):
        self.assertEqual(HydraOS().guide(False, None, None, None, 0), [("enter",), ("open_bp",)])

    def test_dentro_jogo_para_bp(self):
        self.assertEqual(HydraOS().guide(True, G, BP, G, 0), [("focus", BP)])

    def test_dentro_bp_para_jogo(self):
        self.assertEqual(HydraOS().guide(True, BP, BP, G, 0), [("focus", G)])

    def test_dentro_jogo_sem_bp_abre(self):
        self.assertEqual(HydraOS().guide(True, G, None, G, 0), [("open_bp",)])

    def test_guide_durante_carregamento(self):
        h = HydraOS(); h.launched("X", 0)
        self.assertEqual(h.guide(False, None, None, None, 1), [("enter",)])  # de fora: só mostra o carregamento
        # de dentro: saída de emergência; o "Iniciando X…" não pode sobrar para o próximo intervalo
        self.assertEqual(h.guide(True, None, None, None, 2), [("text", ""), ("open_bp",)])
        self.assertEqual(h.phase, "home")

    def test_de_fora_sem_bp_com_jogo_vivo_foca_o_jogo(self):
        h = HydraOS(game=G); h.focused("bp")  # o BP sumiu (o Neo fecha o BP ao lançar)
        self.assertEqual(h.guide(False, None, None, G, 0), [("enter",), ("focus", G)])


class LaunchTest(unittest.TestCase):
    def test_lancamento_mostra_o_nome(self):
        h = HydraOS()
        self.assertEqual(h.launched("ELDEN RING", 0), [("text", "Iniciando ELDEN RING…")])
        self.assertEqual(h.bp_closed(True, 5), [])
        self.assertEqual(h.tick(10), [])

    def test_lancamento_sem_nome(self):
        self.assertEqual(HydraOS().launched(None, 0), [("text", "Iniciando…")])

    def test_janela_do_jogo_limpa_o_texto(self):
        h = HydraOS(); h.launched("X", 0)
        self.assertEqual(h.window(G, "game", True, 8), [("text", "")])
        self.assertEqual((h.phase, h.game), ("playing", G))

    def test_timeout_sem_processo(self):
        h = HydraOS(); h.launched("X", 0)
        self.assertEqual(h.tick(89), [])
        self.assertEqual(h.tick(91), [("text", ""), ("open_bp",)])

    def test_processo_vivo_nao_expira(self):
        h = HydraOS(); h.launched("X", 0); h.process(True, 5)
        self.assertEqual(h.tick(600), [])

    def test_dialogo_segura_a_tela_base(self):
        h = HydraOS(); h.launched("X", 0)
        self.assertEqual(h.window("0xd", "dialog", True, 3), [("hold", True)])
        self.assertEqual(h.window("0xd", "dialog", False, 9), [("hold", False)])


class ExitTest(unittest.TestCase):
    def test_jogo_fechou_volta_ao_bp(self):
        h = HydraOS(game=G)
        self.assertEqual(h.window(G, "game", False, 100), [])
        self.assertEqual(h.tick(101), [])
        self.assertEqual(h.tick(101.6), [("open_bp",)])
        self.assertEqual(h.phase, "home")

    def test_launcher_troca_de_janela(self):
        h = HydraOS(game=G); h.window(G, "game", False, 100); h.window("0xg2", "game", True, 100.8)
        self.assertEqual(h.tick(102), [])

    def test_processo_fechou_sem_janela_volta_ao_bp(self):
        h = HydraOS(); h.launched("X", 0); h.process(True, 5)
        self.assertEqual(h.process(False, 50), [("text", ""), ("open_bp",)])

    def test_sair_do_bp_vai_ao_desktop(self):
        h = HydraOS()
        self.assertEqual(h.bp_closed(True, 0), [])
        self.assertEqual(h.tick(0.5), [])
        self.assertEqual(h.tick(1.1), [("leave",)])

    def test_launched_logo_depois_do_bp_fechar(self):
        h = HydraOS(); h.bp_closed(True, 0); h.launched("X", 0.4)
        self.assertEqual(h.tick(1.1), [])

    def test_sair_do_bp_com_jogo_rodando(self):
        self.assertEqual(HydraOS(game=G).bp_closed(True, 0), [("focus", G)])

    def test_neo_caiu_reabre(self):
        self.assertEqual(HydraOS().bp_closed(False, 0), [("restart_neo",)])

    def test_daemon_reiniciado_no_meio_do_jogo(self):
        h = HydraOS(game=G)
        self.assertEqual(h.phase, "playing")
        self.assertEqual(h.guide(False, None, None, G, 0), [("enter",), ("focus", G)])


if __name__ == "__main__":
    unittest.main()
