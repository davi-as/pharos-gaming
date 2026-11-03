"""Lógica dos botões de um controle: máquina de estados pura, sem I/O.

O daemon entrega cada evento de tecla (key) e chama tick() enquanto `busy`; os dois
devolvem uma lista de ações para ele executar. `now` é sempre injetado (segundos,
relógio monotônico), o que deixa a lógica testável (test_buttons.py).

Gestos: ("bp",) Guide toque, ("suspend",) Guide segurar, ("tv_pc",) Guide+Start, ("chord",) View+Start, ("pharos",) Guide+View,
        ("screenshot",) Share, ("vol", key) ("vol_repeat", key) Guide+RB/LB, ("screen", "l"|"r") Guide+D-pad,
        ("notify", texto, ms).
O daemon troca cada gesto pela ação configurada em ~/.config/console/buttons.env (o PHAROS GAMING edita).
"""

# Xbox (hid-microsoft) e 8BitDo Pro 2 em modo Switch (hid-nintendo) mandam os mesmos códigos:
# Guide/Home=BTN_MODE, View/Minus=BTN_SELECT, Start/Plus=BTN_START, LB/RB=BTN_TL/BTN_TR.
GUIDE, SELECT, START, LB, RB = 316, 314, 315, 310, 311
# Share do Xbox = KEY_RECORD; Capture do 8BitDo = KEY_RECORD ou BTN_Z conforme o kernel.
# BTN_Z só vale no Pro Controller: o Xbox também anuncia BTN_Z.
SHARE, CAPTURE_ALT = 167, 309
HAT0X = 16  # D-pad horizontal: eixo (EV_ABS) nos dois controles, -1 esquerda / 1 direita
PRO = "Pro Controller"

# toque < TAP_MAX -> BP; segurar HOLD_SUSPEND -> suspende (entre os dois: aviso, soltar cancela).
# Abaixo dos ~3,2s em que o 8BitDo se desliga segurando Home (o Xbox aguenta ~6s).
TAP_MAX, HOLD_SUSPEND = 0.8, 2.0
VOL_DELAY, VOL_RATE = 0.4, 0.12  # segurar RB/LB: espera 0,4s, depois repete a cada 0,12s
CHORD_GAP = 0.15  # Start antes do View (ordem invertida no scan do controle) ainda vale


class Shared:
    """Debounce comum a todos os controles (Guide e chord) + o que vem do buttons.env."""
    last = float("-inf")

    def __init__(self, hold=HOLD_SUSPEND, hold_text="suspender", volume=True):
        self.hold = hold  # None: segurar o Guide não faz nada
        self.hold_text = hold_text
        self.volume = volume


class Pad:
    def __init__(self, name, shared):
        self.name, self.shared = name, shared
        self.view_held = False
        self.start_pending = None  # Start sem View, esperando o View chegar
        self.guide_down = None  # Guide apertado desde (None = solto ou já consumido)
        self.guide_held = False  # Guide fisicamente apertado (volume repete sem soltar o Guide)
        self.guide_warned = False
        self.last_shot = float("-inf")
        self.vol_key, self.vol_next = None, 0.0

    @property
    def busy(self):
        """Precisa de tick() frequente (mede tempo de botão segurado)."""
        return self.guide_down is not None or self.vol_key is not None

    def _chord(self, now):
        self.start_pending = None
        if now - self.shared.last > 0.4:
            self.shared.last = now
            return [("chord",)]
        return []

    def key(self, c, v, now):
        if v == 1 and (c == SHARE or (c == CAPTURE_ALT and self.name == PRO)):
            if now - self.last_shot > 1:
                self.last_shot = now
                return [("screenshot",)]
            return []
        if c == SELECT and v == 1 and self.guide_down is not None:  # Guide + View: consome o Guide (sem toque/segurar)
            self.guide_down = None
            self.view_held = True
            return [("pharos",)]
        if c == SELECT:
            self.view_held = bool(v)
            if v == 1 and self.start_pending is not None and now - self.start_pending < CHORD_GAP:
                return self._chord(now)
        elif c == START and v == 1:
            if self.guide_down is not None:  # Guide + Start: consome o Guide (sem toque/segurar)
                self.guide_down = None
                return [("tv_pc",)]
            if self.view_held:
                return self._chord(now)
            self.start_pending = now
        elif c in (LB, RB) and v == 1 and self.guide_held and self.shared.volume:
            # Guide + RB/LB: consome o Guide, mas dá pra apertar RB/LB de novo sem soltá-lo
            out = [("notify", "Suspensão cancelada", 1000)] if self.guide_warned else []
            self.guide_down, self.guide_warned = None, False
            self.vol_key = "KEY_VOLUP" if c == RB else "KEY_VOLDOWN"
            self.vol_next = now + VOL_DELAY
            return out + [("vol", self.vol_key)]
        elif c in (LB, RB) and v == 0:
            self.vol_key = None
        elif c == GUIDE and v == 1:
            self.guide_down, self.guide_held, self.guide_warned = now, True, False
        elif c == GUIDE and v == 0:
            self.guide_held = False
            if self.guide_down is not None:  # toque curto -> BP ao SOLTAR (segurar = suspender)
                held, self.guide_down = now - self.guide_down, None
                if self.guide_warned:
                    return [("notify", "Suspensão cancelada", 1500)]
                if held < TAP_MAX and now - self.shared.last > 1:
                    self.shared.last = now
                    return [("bp",)]
        return []

    def hat(self, c, v, now):
        """Guide + D-pad esquerda/direita: consome o Guide (como o volume, repete sem soltá-lo)."""
        if c != HAT0X or not v or not self.guide_held:
            return []
        out = [("notify", "Suspensão cancelada", 1000)] if self.guide_warned else []
        self.guide_down, self.guide_warned = None, False
        return out + [("screen", "r" if v > 0 else "l")]

    def tick(self, now):
        out = []
        if self.vol_key and now >= self.vol_next:
            self.vol_next = now + VOL_RATE
            out.append(("vol_repeat", self.vol_key))
        if self.guide_down is not None:
            held = now - self.guide_down
            hold = self.shared.hold
            if hold is not None and held >= hold:
                self.guide_down = None
                out.append(("suspend",))
            elif hold is not None and held >= TAP_MAX and not self.guide_warned:
                self.guide_warned = True
                out.append(("notify", f"Continue segurando para {self.shared.hold_text} (solte para cancelar)", 2500))
        return out


# --- buttons.env: o que cada gesto faz (KEY=valor, como o config.env) ---
DEFAULTS = {"GUIDE_TAP": "hydra", "GUIDE_HOLD": "suspend", "GUIDE_START": "tv_pc", "VIEW_START": "mangohud", "GUIDE_VIEW": "pharos_tv",
            "SHARE": "screenshot", "GUIDE_VOLUME": "1", "HOLD_SECONDS": "2"}
GESTURES = {"bp": "GUIDE_TAP", "suspend": "GUIDE_HOLD", "tv_pc": "GUIDE_START", "chord": "VIEW_START", "screenshot": "SHARE",
            "pharos": "GUIDE_VIEW"}
# ação -> verbo do aviso ("Continue segurando para ...")
LABELS = {"hydra": "abrir o Hydra", "suspend": "suspender", "tv_pc": "mudar a TV para o PC", "mangohud": "mostrar o MangoHud",
          "screenshot": "capturar a tela", "steam_bp": "abrir o Steam Big Picture", "pharos_tv": "abrir o menu do Pharos", "none": ""}


def load_config(path):
    """Devolve (config, avisos). Chave ou ação desconhecida fica no padrão: um arquivo ruim não tira o controle."""
    cfg, warnings = dict(DEFAULTS), []
    try:
        with open(path) as f:
            for line in f:
                k, sep, v = line.strip().partition("=")
                if sep and k in DEFAULTS:
                    cfg[k] = v.strip()
    except OSError:
        pass
    for k in GESTURES.values():
        if cfg[k] not in LABELS:
            warnings.append(f"buttons.env: ação desconhecida {k}={cfg[k]}, usando {DEFAULTS[k]}")
            cfg[k] = DEFAULTS[k]
    return cfg, warnings


def shared_from(cfg):
    try:
        hold = min(max(float(cfg["HOLD_SECONDS"]), 1.0), 3.0)  # acima de ~3,2s o 8BitDo se desliga
    except ValueError:
        hold = HOLD_SUSPEND
    return Shared(hold=None if cfg["GUIDE_HOLD"] == "none" else hold,
                  hold_text=LABELS[cfg["GUIDE_HOLD"]], volume=cfg["GUIDE_VOLUME"] != "0")
