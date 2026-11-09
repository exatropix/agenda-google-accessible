#!/bin/sh
# Compile l'exécutable Windows (.NET Framework 4.6.2+, présent dans Windows 10/11) avec Mono.
# Fichiers facultatifs intégrés au programme s'ils sont présents dans ce dossier (jamais publiés dans le dépôt) :
#   agenda-identifiants.json    identifiants Google de l'application
#   lib/nvdaControllerClient32.dll et lib/nvdaControllerClient64.dll   bibliothèque de NVDA pour parler
set -e
cd "$(dirname "$0")"
API=/usr/lib/mono/4.6.2-api
RES=""
[ -f agenda-identifiants.json ] && RES="$RES -resource:agenda-identifiants.json,agenda-identifiants.json"
[ -f lib/nvdaControllerClient32.dll ] && RES="$RES -resource:lib/nvdaControllerClient32.dll,nvdaControllerClient32.dll"
[ -f lib/nvdaControllerClient64.dll ] && RES="$RES -resource:lib/nvdaControllerClient64.dll,nvdaControllerClient64.dll"
mkdir -p out
mcs -target:winexe -out:out/AgendaGoogleAccessible.exe -langversion:7 -optimize+ -nostdlib -noconfig -nowarn:414 \
 -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Windows.Forms.dll -r:$API/System.Drawing.dll \
 -r:$API/System.Net.Http.dll -r:$API/System.Web.Extensions.dll -r:$API/System.Security.dll -r:$API/System.IO.Compression.dll -r:$API/System.IO.Compression.FileSystem.dll \
 $RES src/*.cs
cp app.config out/AgendaGoogleAccessible.exe.config
