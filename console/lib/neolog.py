"""Log do Hydra Neo: lançamento e fim de jogo, e o nome do jogo para a tela de carregamento do Hydra OS.

Linhas do ~/.config/hydralauncher-native/logs/info.txt (Neo 4.4.0):
  [2026-11-21 13:22:35.670] [info]  (main)         Launched steam:1245620 through Steam
  [2026-11-21 13:22:42.620] [info]  (main)         [ProcessWatcher] steam:1245620 opened (/…/eldenring.exe)
  [2026-11-21 13:24:32.471] [info]  (main)         [ProcessWatcher] steam:1245620 closed (+2s)
"""
import os, re

HOME = os.path.expanduser("~")
INFO_LOG = f"{HOME}/.config/hydralauncher-native/logs/info.txt"
STEAM_VDF = f"{HOME}/.local/share/Steam/steamapps/libraryfolders.vdf"

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
