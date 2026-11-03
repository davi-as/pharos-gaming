#!/usr/bin/env bash
# PC como console: instala ou atualiza o daemon do controle (serviço xbox-guide), os scripts que ele e o PHAROS usam
# (tv-power, hydra, pc-suspend, controller-screenshot, reconexão do Xbox) e confere o que mora fora do repositório:
# os valores da máquina (config.env), o MANGOHUD=1 no atalho da Steam e as linhas do autostart do Hyprland.
# Só troca o que mudou, com backup (.bak.<data>) do anterior. Rode de novo depois de atualizar o código.
#   scripts/install-console.sh           instala/atualiza e corrige o que der
#   scripts/install-console.sh --check   só confere e lista o que está diferente, sem mudar nada
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/console"
check=0
[[ "${1:-}" == "--check" ]] && check=1
stamp=$(date +%s)
pending=0      # algo diferente do repositório (no --check) ou que precisa de atenção
daemon_new=0   # o daemon mudou: reiniciar o serviço
units_new=0    # algum serviço mudou: daemon-reload

say() { printf '  %-10s %s\n' "$1" "$2"; }

# put ORIGEM DESTINO MODO: 0 se instalou agora
put() {
	if [[ -f "$2" ]] && cmp -s "$1" "$2"; then say ok "$2"; return 1; fi
	if ((check)); then say diferente "$2"; pending=1; return 1; fi
	[[ -f "$2" ]] && cp "$2" "$2.bak.$stamp"
	install -Dm"$3" "$1" "$2"
	say instalado "$2"
}

echo "Arquivos:"
for f in daemon.py buttons.py hypr.py test_buttons.py e2e_virtual_pad.py; do
	put "$src/lib/$f" "$HOME/.local/lib/console/$f" 644 && daemon_new=1
done
for f in tv-power pc-suspend controller-screenshot hydra xbox-bt-autoconnect.sh; do
	put "$src/bin/$f" "$HOME/.local/bin/$f" 755 || true
done
for f in xbox-guide.service xbox-bt-autoconnect.service; do
	put "$src/systemd/$f" "$HOME/.config/systemd/user/$f" 644 && units_new=1
done

echo "Valores da máquina (~/.config/console/config.env):"
env_file="$HOME/.config/console/config.env"
for key in TV_IP TV_MAC XBOX_MAC; do
	if [[ -f "$env_file" ]] && grep -q "^$key=." "$env_file"; then say ok "$key"; else say falta "$key (tv-power e reconexão do Xbox dependem dele)"; pending=1; fi
done

echo "MangoHud nos jogos da Steam:"
# os jogos da Steam nascem dela: sem MANGOHUD=1 no ambiente da Steam, o PHAROS não vê o jogo nem o FPS
desktop="$HOME/.local/share/applications/steam.desktop"
if [[ ! -f "$desktop" ]]; then
	say aviso "sem $desktop: a Steam aberta pelo menu de aplicativos não passa o MangoHud aos jogos"
	pending=1
elif grep '^Exec=' "$desktop" | grep -qv 'MANGOHUD=1'; then
	if ((check)); then say diferente "$desktop (Exec sem MANGOHUD=1)"; pending=1
	else
		cp "$desktop" "$desktop.bak.$stamp"
		sed -i '/^Exec=/{/MANGOHUD=1/!s/^Exec=/Exec=env MANGOHUD=1 /}' "$desktop"
		say corrigido "$desktop"
	fi
else
	say ok "$desktop"
fi

echo "Autostart do Hyprland (~/.config/hypr/autostart.lua):"
autostart="$HOME/.config/hypr/autostart.lua"
pharos_line='o.launch_on_start("'"$HOME"'/.local/share/pharos-gaming/app/pharos-gaming --tv --hidden")'
if [[ ! -f "$autostart" ]]; then
	say aviso "sem $autostart"
	pending=1
else
	grep -q 'hydra --hidden' "$autostart" && say ok "Hydra" || { say falta "Hydra (hydra --hidden)"; pending=1; }
	if ! grep -q 'steam -silent' "$autostart"; then say falta "Steam (steam -silent)"; pending=1
	elif ! grep 'steam -silent' "$autostart" | grep -q 'MANGOHUD=1'; then
		say aviso "Steam sem MANGOHUD=1 (liga em PC como console, no PHAROS)"; pending=1
	else say ok "Steam com MANGOHUD=1"; fi
	if grep -qF -- '--tv --hidden' "$autostart"; then say ok "PHAROS escondido"
	elif ((check)); then say falta "PHAROS escondido (--tv --hidden)"; pending=1
	else
		cp "$autostart" "$autostart.bak.$stamp"
		printf '%s\n%s\n' "-- Pharos Gaming: menu do controle (Guide + View) residente e escondido; mede o jogo desde o início" "$pharos_line" >>"$autostart"
		say adicionado "PHAROS escondido (vale no próximo login)"
	fi
fi

if ((!check)); then
	echo "Serviços:"
	((units_new)) && systemctl --user daemon-reload
	systemctl --user enable --now xbox-guide.service xbox-bt-autoconnect.service >/dev/null 2>&1 || true
	((daemon_new)) && systemctl --user restart xbox-guide.service
	for u in xbox-guide xbox-bt-autoconnect; do
		systemctl --user is-active --quiet "$u" && say ok "$u" || { say parado "$u (journalctl --user -u $u)"; pending=1; }
	done
fi

if ((pending)); then echo "Há itens acima para conferir."; exit 1; fi
echo "Tudo certo."
