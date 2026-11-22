"""Hydra OS: a área 4 da TV como console. Máquina de estados pura, sem I/O (como buttons.py).

O daemon entrega fatos (Guide, log do Neo, janelas do Hyprland, relógio) e executa as ações devolvidas:
  ("enter",)          liga a TV, foca a TV e a área 4, cursor no canto
  ("focus", addr)     foca a janela em tela cheia
  ("open_bp",)        abre o Big Picture (o Neo repassa para a instância aberta)
  ("restart_neo",)    o Neo morreu: abre de novo direto no BP
  ("leave",)          TV para a área 3 ("Sair do Big Picture" no menu do BP)
  ("text", str)       texto da tela base ("" = só preto)
  ("art", fundo, logo) arte do jogo na tela de carregamento (caminhos; "" = sem arte)
  ("hold", bool)      diálogo da Steam no carregamento: tela base transparente enquanto ele existir
`now` é sempre injetado (segundos, relógio monotônico): a lógica fica testável (test_hydraos.py).
"""

LAUNCH_TIMEOUT = 90.0  # lançou e o processo do jogo não apareceu: volta ao BP
EXIT_GRACE = 1.5  # janela do jogo fechou: espera, pode ser o launcher trocando para o jogo
CLOSE_GRACE = 1.0  # BP fechou sem "Launched" no log ainda: espera antes de concluir que você saiu do BP
GONE_MAX = 30.0  # janela do jogo sumiu com o processo vivo (launcher lento, jogo minimizado): espera até isso
BP_TITLE = "Hydra Big Picture"


def backdrop_state(visible, windows, hold, launching=False):
    """Tela base: "off" fora da área 4; "cover" (preto por cima de tudo) só com a área 4 vazia ou só com a janela
    principal do Neo (que vai para a bandeja); "base" (preto por baixo) com qualquer outra janela, em tela cheia ou não:
    um emulador ou jogo em janela não pode ficar sob o preto. hold: diálogo da Steam segurado no carregamento.
    launching: a tela de carregamento cobre também o BP (o Neo o deixa aberto ao lançar) até a janela do jogo abrir."""
    if not visible:
        return "off"
    if hold:
        return "base"
    neo = lambda c: c.get("class") == "hydralauncher"
    if launching:
        # janelas da Steam no carregamento (avisos "notificationtoasts", "Launching...") não são o jogo; os diálogos
        # que importam chegam pelo hold
        return "base" if any(not neo(c) and c.get("class") != "steam" for c in windows) else "cover"
    neo_main = lambda c: neo(c) and c.get("title") != BP_TITLE
    return "base" if any(not neo_main(c) for c in windows) else "cover"


def stuck_top_layers(layers_text, monitor):
    """Camadas do nível "top" visíveis (alpha 1) no monitor, tirando a tela base do Hydra OS, a partir da saída de
    `hyprctl layers`. Chamada com um jogo ou o BP em tela cheia na frente, cada uma está presa POR CIMA dele: no
    Hyprland 0.56.2 uma camada recém-mapeada (a barra do Omarchy quando a TV religa) fica acima da tela cheia."""
    out, mon, level = [], None, None
    for line in layers_text.splitlines():
        if line.startswith("Monitor "):
            mon = line[len("Monitor "):].rstrip(":")
        elif "Layer level" in line:
            level = line.split("Layer level", 1)[1].split()[0]
        elif mon == monitor and level == "2" and "namespace: " in line and ", a: 1," in line:
            ns = line.split("namespace: ", 1)[1].split(",")[0]
            if not ns.startswith("hydraos-"):
                out.append(ns)
    return out


class HydraOS:
    def __init__(self, game=None):
        """game: janela de jogo que já está na área 4 quando o daemon sobe (reinício no meio do jogo)."""
        self.game = game
        self.phase = "playing" if game else "home"  # "home" | "launching" | "playing"
        self.last = "game" if game else "bp"  # o que estava em foco por último na área 4
        self.text = ""
        self.art = False  # a tela base está com a arte de um jogo
        self.launch_at = self.exit_at = self.closed_at = None
        self.proc = False  # o Neo viu o processo do jogo (ProcessWatcher opened)
        self.game_id = None  # id do Neo do último lançamento ("steam:1245620"): linhas de outro jogo são ignoradas

    def _clear_text(self):
        out = [("text", "")] if self.text else []
        if self.art:
            out.append(("art", "", ""))
        self.text, self.art = "", False
        return out

    def _home(self):
        self.phase, self.launch_at = "home", None

    def guide(self, inside, focused, bp, game, now):
        """inside: a TV está na área 4 e com o foco. focused/bp/game: endereços vivos agora (ou None)."""
        if self.phase == "launching":
            if not inside:
                return [("enter",)]  # o carregamento continua na tela
            self._home()
            return self._clear_text() + [("open_bp",)]  # saída de emergência
        if not inside:
            if game and (self.last == "game" or not bp):
                return [("enter",), ("focus", game)]
            return [("enter",), ("focus", bp) if bp else ("open_bp",)]
        if bp and focused == bp:
            return [("focus", game)] if game else []
        return [("focus", bp)] if bp else [("open_bp",)]

    def launched(self, name, now, game_id=None, hero=None, logo=None):
        """Log do Neo: 'Launched <id>'. O BP ainda aparece; texto e arte ficam prontos para quando ele fechar."""
        self.phase, self.launch_at, self.proc, self.closed_at = "launching", now, False, None
        self.game_id = game_id
        self.text = f"Iniciando {name}…" if name else "Iniciando…"
        out = [("text", self.text)]
        if hero or logo or self.art:
            self.art = bool(hero or logo)
            out.append(("art", hero or "", logo or ""))
        return out

    def process(self, running, now, game_id=None):
        """Log do Neo: ProcessWatcher opened/closed (ou o daemon vendo o Proton/umu subir, sem id)."""
        if game_id and self.game_id and game_id != self.game_id:
            return []  # "closed" atrasado do jogo anterior não cancela o lançamento de agora
        self.proc = running
        if running or self.phase == "home" or self.game:
            return []  # com janela de jogo viva, quem decide é o fechamento da janela
        self._home()
        return self._clear_text() + [("open_bp",)]

    def bp_closed(self, neo_alive, now, restarting=False):
        """restarting: o daemon está reiniciando o Neo (8BitDo reconectou): o BP volta sozinho, não é saída nem queda."""
        if restarting:
            return []
        if not neo_alive:
            self._home()
            return self._clear_text() + [("restart_neo",)]
        if self.phase == "launching":
            return []  # o Neo fecha o BP ao lançar: a tela base mostra o "Iniciando…"
        if self.game:
            return [("focus", self.game)]  # saiu do BP com o jogo rodando atrás
        self.closed_at = now  # o "Launched" pode chegar logo depois: o tick decide
        return []

    def window(self, addr, kind, opened, now, remaining=None):
        """remaining: ao fechar, outra janela de jogo que continua na área 4 (o jogo não acabou: ela vira o jogo)."""
        if kind == "dialog":
            return [("hold", opened)]
        if opened:
            self.game, self.exit_at, self.last = addr, None, "game"
            self.phase, self.launch_at = "playing", None
            return self._clear_text()
        if addr == self.game:
            self.game, self.exit_at = (remaining, None) if remaining else (None, now)
        return []

    def focused(self, kind):
        self.last = kind

    def tick(self, now):
        out = []
        if self.phase == "launching" and not self.proc and now - self.launch_at > LAUNCH_TIMEOUT:
            self._home()
            out += self._clear_text() + [("open_bp",)]
        if self.exit_at is not None and now - self.exit_at >= EXIT_GRACE and not (self.proc and now - self.exit_at < GONE_MAX):
            # processo vivo: quem encerra é o "closed" do Neo (process(False)), ou o limite GONE_MAX
            self.exit_at = None
            if self.game is None and self.phase == "playing":
                self._home()
                out.append(("open_bp",))
        if self.closed_at is not None and now - self.closed_at >= CLOSE_GRACE:
            self.closed_at = None
            if self.phase == "home" and self.game is None:
                out.append(("leave",))
        return out
