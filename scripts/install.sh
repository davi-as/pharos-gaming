#!/usr/bin/env bash
# Instala o Pharos Gaming para o usuário: app publicado (self-contained, não precisa do .NET instalado),
# ícone e atalho no menu de aplicativos. Rode de novo depois de atualizar o código.
# Remover: scripts/install.sh --uninstall
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
app="$data/pharos-gaming/app"
desktop="$data/applications/pharos-gaming.desktop"
icons="$data/icons/hicolor"

if [[ "${1:-}" == "--uninstall" ]]; then
	rm -rf "$app" "$desktop" "$icons/scalable/apps/pharos-gaming.svg" "$icons/512x512/apps/pharos-gaming.png" "$icons/192x192/apps/pharos-gaming.png"
	echo "Pharos Gaming removido (configurações em ~/.config/pharos-gaming ficaram)."
	exit 0
fi

dotnet=(dotnet)
command -v dotnet >/dev/null || dotnet=(mise exec -C "$root" -- dotnet)

echo "Publicando em $app…"
rm -rf "$app"
"${dotnet[@]}" publish "$root/src" -c Release -r linux-x64 --self-contained true -o "$app" -p:DebugType=none --nologo -v quiet

install -Dm644 "$root/src/Assets/icon.svg" "$icons/scalable/apps/pharos-gaming.svg"
install -Dm644 "$root/src/Assets/icon-512.png" "$icons/512x512/apps/pharos-gaming.png"
install -Dm644 "$root/src/Assets/icon-192.png" "$icons/192x192/apps/pharos-gaming.png"

# StartupWMClass = app_id da janela no Wayland (nome do executável): liga a janela ao ícone do atalho
install -Dm644 /dev/stdin "$desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Pharos Gaming
GenericName=Monitor de desempenho para jogos
Comment=FPS, sensores, gargalo, benchmarks e ferramentas para jogar no Linux
Exec=$app/pharos-gaming
Icon=pharos-gaming
Terminal=false
Categories=Game;
Keywords=fps;benchmark;gpu;cpu;sensores;mangohud;overclock;steam;
StartupWMClass=pharos-gaming
DESKTOP

command -v update-desktop-database >/dev/null && update-desktop-database -q "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$icons" || true
echo "Pronto: procure \"Pharos Gaming\" no menu de aplicativos."
