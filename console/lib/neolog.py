"""Log do Hydra Neo: lançamento e fim de jogo, e o nome do jogo para a tela de carregamento do Hydra OS.

Linhas do ~/.config/hydralauncher-native/logs/info.txt (Neo 4.4.0):
  [2026-11-21 13:22:35.670] [info]  (main)         Launched steam:1245620 through Steam
  [2026-11-21 13:22:42.620] [info]  (main)         [ProcessWatcher] steam:1245620 opened (/…/eldenring.exe)
  [2026-11-21 13:24:32.471] [info]  (main)         [ProcessWatcher] steam:1245620 closed (+2s)
"""
import json, os, re

HOME = os.path.expanduser("~")
INFO_LOG = f"{HOME}/.config/hydralauncher-native/logs/info.txt"
STEAM_VDF = f"{HOME}/.local/share/Steam/steamapps/libraryfolders.vdf"
STEAM_ART = f"{HOME}/.local/share/Steam/appcache/librarycache"  # <appid>/library_hero.jpg e logo.png
ART_CACHE = f"{HOME}/.cache/hydra-os/art.json"  # jogos personalizados: custom-games.mjs art (cópia do banco do Neo)

_LAUNCHED = re.compile(r"\bLaunched (\S+)")  # jogo que não é da Steam pode não ter " through ..." depois
_WATCHER = re.compile(r"\[ProcessWatcher\] (\S+) (opened|closed)\b")


def parse(line):
    """("launched" | "opened" | "closed", id do jogo), ou None para as outras linhas."""
    if m := _WATCHER.search(line):
        return m.group(2), m.group(1)
    if m := _LAUNCHED.search(line):
        return "launched", m.group(1)
    return None


def _read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def game_name(game_id, vdf=STEAM_VDF):
    """Nome do jogo da Steam pelo appmanifest da biblioteca onde ele está instalado.
    shortcut: jogos que não são da Steam ficam sem nome ("Iniciando…"); ler do banco do Neo quando valer a pena."""
    shop, _, appid = game_id.partition(":")
    if shop != "steam" or not appid.isdigit():
        return None
    for lib in re.findall(r'"path"\s+"([^"]+)"', _read(vdf)):
        if m := re.search(r'"name"\s+"([^"]+)"', _read(f"{lib}/steamapps/appmanifest_{appid}.acf")):
            return m.group(1)
    return None


def _file(path):
    return path if path and os.path.isfile(path) else None


def game_info(game_id, vdf=STEAM_VDF, steam_art=STEAM_ART, art_cache=ART_CACHE):
    """(nome, imagem de fundo, logo) para a tela de carregamento; o que faltar vem None.
    Steam: appmanifest + cache de artes da própria Steam. Outros (custom:<uuid>): o art.json que o
    custom-games.mjs tira de uma cópia do banco do Neo (as artes que o hydra-art baixou)."""
    shop, _, appid = game_id.partition(":")
    if shop == "steam":
        return game_name(game_id, vdf), _file(f"{steam_art}/{appid}/library_hero.jpg"), _file(f"{steam_art}/{appid}/logo.png")
    try:
        with open(art_cache) as f:
            g = json.load(f).get(game_id) or {}
    except (OSError, ValueError):
        g = {}
    return g.get("title"), _file(g.get("hero")), _file(g.get("logo"))


class Follower:
    """Como `tail -F`: read() devolve as linhas completas novas. Começa no fim do arquivo (o que já estava
    escrito é passado); se o arquivo encolher (log recriado) relê do zero; arquivo que ainda não existe é lido
    do início quando aparecer."""

    def __init__(self, path):
        self.path = path
        try:
            self.pos = os.path.getsize(path)
        except OSError:
            self.pos = 0

    def read(self):
        try:
            with open(self.path, "rb") as f:
                size = os.fstat(f.fileno()).st_size
                if size < self.pos:
                    self.pos = 0
                f.seek(self.pos)
                data = f.read()
        except OSError:
            return []
        end = data.rfind(b"\n") + 1  # linha sem \n ainda está sendo escrita: fica para a próxima
        self.pos += end
        return data[:end].decode(errors="replace").splitlines()
