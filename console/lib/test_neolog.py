#!/usr/bin/env python3
# Testes da leitura do log do Hydra Neo: python3 ~/.local/lib/console/test_neolog.py
import os, tempfile, unittest

from neolog import Follower, game_info, game_name, parse

P = "[2026-11-21 13:22:35.670] [info]  (main)         "


def write(path, s, mode="w"):
    with open(path, mode) as f:
        f.write(s)


class ParseTest(unittest.TestCase):
    def test_launched(self):
        self.assertEqual(parse(P + "Launched steam:1245620 through Steam"), ("launched", "steam:1245620"))

    def test_opened(self):
        self.assertEqual(parse(P + "[ProcessWatcher] steam:1245620 opened (/mnt/Alexandria/steamapps/common/ELDEN RING/Game/eldenring.exe)"),
                         ("opened", "steam:1245620"))

    def test_closed(self):
        self.assertEqual(parse(P + "[ProcessWatcher] steam:1245620 closed (+2s)"), ("closed", "steam:1245620"))

    def test_launched_sem_texto_depois_do_id(self):
        self.assertEqual(parse(P + "Launched custom:abc"), ("launched", "custom:abc"))

    def test_outras_linhas(self):
        self.assertIsNone(parse(P + "Screenshot input started: keyboard evdev gamepad evdev hint None"))
        self.assertIsNone(parse(P + "Steam protocol launch delegates command wrappers to Steam settings: steam:1245620"))


class FilesTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name

    def tearDown(self):
        self._tmp.cleanup()

    def test_nome_pela_steam(self):
        lib = os.path.join(self.tmp, "lib")
        os.makedirs(os.path.join(lib, "steamapps"))
        vdf = os.path.join(self.tmp, "libraryfolders.vdf")
        write(vdf, '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n}\n' % lib)
        write(os.path.join(lib, "steamapps", "appmanifest_1245620.acf"), '"AppState"\n{\n\t"appid"\t\t"1245620"\n\t"name"\t\t"ELDEN RING"\n}\n')
        self.assertEqual(game_name("steam:1245620", vdf), "ELDEN RING")
        self.assertIsNone(game_name("steam:999", vdf))
        self.assertIsNone(game_name("custom:abc", vdf))
        self.assertIsNone(game_name("steam:1245620", os.path.join(self.tmp, "nao-existe.vdf")))

    def test_arte_da_steam(self):
        lib = os.path.join(self.tmp, "lib")
        os.makedirs(os.path.join(lib, "steamapps"))
        vdf = os.path.join(self.tmp, "libraryfolders.vdf")
        write(vdf, '"path"\t\t"%s"\n' % lib)
        write(os.path.join(lib, "steamapps", "appmanifest_1245620.acf"), '"name"\t\t"ELDEN RING"\n')
        cache = os.path.join(self.tmp, "librarycache", "1245620")
        os.makedirs(cache)
        for f in ("library_hero.jpg", "logo.png"):
            write(os.path.join(cache, f), "x")
        self.assertEqual(game_info("steam:1245620", vdf, os.path.join(self.tmp, "librarycache"), "/nao-existe.json"),
                         ("ELDEN RING", os.path.join(cache, "library_hero.jpg"), os.path.join(cache, "logo.png")))

    def test_arte_de_jogo_personalizado_pelo_cache_do_banco_do_neo(self):
        art = os.path.join(self.tmp, "art.json")
        hero = os.path.join(self.tmp, "hero.png")
        write(hero, "x")
        write(art, '{"custom:19cc": {"title": "Mario Kart 8 Deluxe", "hero": "%s", "logo": "/sumiu.png"}}' % hero)
        self.assertEqual(game_info("custom:19cc", "/nao-existe.vdf", self.tmp, art), ("Mario Kart 8 Deluxe", hero, None))
        self.assertEqual(game_info("custom:outro", "/nao-existe.vdf", self.tmp, art), (None, None, None))

    def test_follower(self):
        path = os.path.join(self.tmp, "info.txt")
        write(path, "a\n")
        f = Follower(path)
        self.assertEqual(f.read(), [])  # começa no fim
        write(path, "b\nc", "a")
        self.assertEqual(f.read(), ["b"])  # linha incompleta espera
        write(path, "\n", "a")
        self.assertEqual(f.read(), ["c"])
        write(path, "d\n")
        self.assertEqual(f.read(), ["d"])  # arquivo encolheu: relê do zero

    def test_follower_sem_arquivo(self):
        path = os.path.join(self.tmp, "info.txt")
        f = Follower(path)
        self.assertEqual(f.read(), [])
        write(path, "x\n")
        self.assertEqual(f.read(), ["x"])  # arquivo criado depois: lê desde o início


if __name__ == "__main__":
    unittest.main()
