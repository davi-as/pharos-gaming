"""Hydra OS: a área 4 da TV como console. Máquina de estados pura, sem I/O (como buttons.py).

O daemon entrega fatos (Guide, log do Neo, janelas do Hyprland, relógio) e executa as ações devolvidas:
  ("enter",)          liga a TV, foca a TV e a área 4, cursor no canto
  ("focus", addr)     foca a janela em tela cheia
  ("open_bp",)        abre o Big Picture (o Neo repassa para a instância aberta)
  ("restart_neo",)    o Neo morreu: abre de novo direto no BP
  ("leave",)          TV para a área 3 ("Sair do Big Picture" no menu do BP)
  ("text", str)       texto da tela base ("" = só preto)
  ("hold", bool)      diálogo da Steam no carregamento: tela base transparente enquanto ele existir
`now` é sempre injetado (segundos, relógio monotônico): a lógica fica testável (test_hydraos.py).
"""

LAUNCH_TIMEOUT = 90.0  # lançou e o processo do jogo não apareceu: volta ao BP
EXIT_GRACE = 1.5  # janela do jogo fechou: espera, pode ser o launcher trocando para o jogo
CLOSE_GRACE = 1.0  # BP fechou sem "Launched" no log ainda: espera antes de concluir que você saiu do BP


class HydraOS:
    def __init__(self, game=None):
        """game: janela de jogo que já está na área 4 quando o daemon sobe (reinício no meio do jogo)."""
        self.game = game
        self.phase = "playing" if game else "home"  # "home" | "launching" | "playing"
        self.last = "game" if game else "bp"  # o que estava em foco por último na área 4
        self.text = ""
        self.launch_at = self.exit_at = self.closed_at = None
        self.proc = False  # o Neo viu o processo do jogo (ProcessWatcher opened)

    def _clear_text(self):
        if not self.text:
            return []
        self.text = ""
        return [("text", "")]

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

    def launched(self, name, now):
        """Log do Neo: 'Launched <id>'. O BP ainda aparece; o texto fica pronto para quando ele fechar."""
        self.phase, self.launch_at, self.proc, self.closed_at = "launching", now, False, None
        self.text = f"Iniciando {name}…" if name else "Iniciando…"
        return [("text", self.text)]

    def process(self, running, now):
        """Log do Neo: ProcessWatcher opened/closed."""
        self.proc = running
        if running or self.phase == "home" or self.game:
            return []  # com janela de jogo viva, quem decide é o fechamento da janela
        self._home()
        return self._clear_text() + [("open_bp",)]

    def bp_closed(self, neo_alive, now):
        if not neo_alive:
            self._home()
            return [("restart_neo",)]
        if self.phase == "launching":
            return []  # o Neo fecha o BP ao lançar: a tela base mostra o "Iniciando…"
        if self.game:
            return [("focus", self.game)]  # saiu do BP com o jogo rodando atrás
        self.closed_at = now  # o "Launched" pode chegar logo depois: o tick decide
        return []

    def window(self, addr, kind, opened, now):
        if kind == "dialog":
            return [("hold", opened)]
        if opened:
            self.game, self.exit_at, self.last = addr, None, "game"
            self.phase, self.launch_at = "playing", None
            return self._clear_text()
        if addr == self.game:
            self.game, self.exit_at = None, now
        return []

    def focused(self, kind):
        self.last = kind

    def tick(self, now):
        out = []
        if self.phase == "launching" and not self.proc and now - self.launch_at > LAUNCH_TIMEOUT:
            self._home()
            out += self._clear_text() + [("open_bp",)]
        if self.exit_at is not None and now - self.exit_at >= EXIT_GRACE:
            self.exit_at = None
            if self.game is None and self.phase == "playing":
                self._home()
                out.append(("open_bp",))
        if self.closed_at is not None and now - self.closed_at >= CLOSE_GRACE:
            self.closed_at = None
            if self.phase == "home" and self.game is None:
                out.append(("leave",))
        return out
