#!/usr/bin/env python3
# Testes da máquina de estados do Hydra OS (área 4 da TV): python3 ~/.local/lib/console/test_hydraos.py
import unittest

from hydraos import BP_TITLE, HydraOS, backdrop_state, stuck_top_layers

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


class ArtTest(unittest.TestCase):
    def test_lancamento_com_arte(self):
        h = HydraOS()
        self.assertEqual(h.launched("ELDEN RING", 0, "steam:1", hero="/h.jpg", logo="/l.png"),
                         [("text", "Iniciando ELDEN RING…"), ("art", "/h.jpg", "/l.png")])
        self.assertEqual(h.window(G, "game", True, 5), [("text", ""), ("art", "", "")])

    def test_arte_some_na_saida_de_emergencia(self):
        h = HydraOS(); h.launched("X", 0, hero="/h.jpg")
        self.assertEqual(h.guide(True, None, None, None, 1), [("text", ""), ("art", "", ""), ("open_bp",)])


class ReviewTest(unittest.TestCase):
    """Achados da revisão final (2026-11-21)."""

    def test_dialogo_do_jogo_fecha_e_a_janela_principal_fica(self):
        h = HydraOS(game=G); h.window("0xg2", "game", True, 10)  # 2ª janela do jogo (ex.: "Confirmar saída?")
        self.assertEqual(h.window("0xg2", "game", False, 12, remaining=G), [])
        self.assertEqual(h.game, G)
        self.assertEqual(h.tick(20), [])

    def test_launcher_demora_com_processo_vivo(self):
        h = HydraOS(); h.launched("X", 0); h.process(True, 5); h.window(G, "game", True, 8)
        h.window(G, "game", False, 100)
        self.assertEqual(h.tick(105), [])  # janela sumiu, processo vivo: espera
        self.assertEqual(h.process(False, 110), [("open_bp",)])

    def test_janela_sumiu_com_processo_vivo_desiste_em_30s(self):
        h = HydraOS(); h.launched("X", 0); h.process(True, 5); h.window(G, "game", True, 8)
        h.window(G, "game", False, 100)
        self.assertEqual(h.tick(129), [])
        self.assertEqual(h.tick(131), [("open_bp",)])

    def test_reinicio_do_hydra_nao_conta_como_sair_do_bp(self):
        h = HydraOS()
        self.assertEqual(h.bp_closed(False, 0, restarting=True), [])
        self.assertEqual(h.bp_closed(True, 0, restarting=True), [])
        self.assertEqual(h.tick(2), [])

    def test_closed_atrasado_de_outro_jogo_e_ignorado(self):
        h = HydraOS(); h.launched("B", 0, "steam:2")
        self.assertEqual(h.process(False, 5, "steam:1"), [])
        self.assertEqual(h.phase, "launching")
        self.assertEqual(h.process(True, 6, "steam:2"), [])
        self.assertTrue(h.proc)


MAIN = {"class": "hydralauncher", "title": "Hydra", "fullscreen": 0}  # janela principal do Neo (vai para a bandeja)
BPW = {"class": "hydralauncher", "title": BP_TITLE, "fullscreen": 2}


class BackdropStateTest(unittest.TestCase):
    def test_fora_da_area_4(self):
        self.assertEqual(backdrop_state(False, [BPW], False), "off")

    def test_area_4_vazia_ou_so_a_janela_principal_do_neo(self):
        self.assertEqual(backdrop_state(True, [], False), "cover")
        self.assertEqual(backdrop_state(True, [MAIN], False), "cover")

    def test_bp_ou_jogo_em_tela_cheia(self):
        self.assertEqual(backdrop_state(True, [BPW], False), "base")
        self.assertEqual(backdrop_state(True, [{"class": "eldenring.exe", "title": "ELDEN RING", "fullscreen": 2}], False), "base")

    def test_emulador_em_janela_nao_fica_sob_o_preto(self):
        self.assertEqual(backdrop_state(True, [{"class": "pcsx2-qt", "title": "PCSX2", "fullscreen": 0}], False), "base")

    def test_dialogo_da_steam_segurado(self):
        self.assertEqual(backdrop_state(True, [], True), "base")


LAYERS = """Monitor HDMI-A-1:
	Layer level 0 (background):
		Layer 555e86d330c0: xywh: -1600 -592 1600 900, a: 1, namespace: omarchy-background, pid: 3127
	Layer level 1 (bottom):
		Layer 555e8b80d400: xywh: -1600 -592 1600 900, a: 1, namespace: hydraos-base, pid: 1
	Layer level 2 (top):
		Layer 555e86234810: xywh: -1600 -592 1600 37, a: %s, namespace: omarchy-bar, pid: 3127
		Layer 555e899339a0: xywh: -1600 -592 1600 900, a: 1, namespace: hydraos-cover, pid: 1
	Layer level 3 (overlay):
		Layer 555e8c000000: xywh: -1600 -592 1600 900, a: 1, namespace: notifications, pid: 3127

Monitor DP-2:
	Layer level 2 (top):
		Layer 555e86ab5ee0: xywh: 360 128 2560 37, a: 1, namespace: omarchy-bar, pid: 3127
"""


class StuckLayersTest(unittest.TestCase):
    def test_barra_presa_por_cima_da_tela_cheia(self):
        self.assertEqual(stuck_top_layers(LAYERS % "1", "HDMI-A-1"), ["omarchy-bar"])

    def test_barra_escondida_e_a_propria_tela_base_nao_contam(self):
        self.assertEqual(stuck_top_layers(LAYERS % "0", "HDMI-A-1"), [])

    def test_so_o_monitor_pedido(self):
        self.assertEqual(stuck_top_layers(LAYERS % "0", "DP-3"), [])


if __name__ == "__main__":
    unittest.main()
