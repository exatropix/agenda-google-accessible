#!/bin/sh
# Compile l'exécutable Windows (.NET Framework 4.6.2+, présent dans Windows 10/11) avec Mono.
set -e
cd "$(dirname "$0")"
API=/usr/lib/mono/4.6.2-api
mkdir -p out
mcs -target:winexe -out:out/AgendaGoogleAccessible.exe -langversion:7 -optimize+ -nostdlib -noconfig -nowarn:414 \
 -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Windows.Forms.dll -r:$API/System.Drawing.dll \
 -r:$API/System.Net.Http.dll -r:$API/System.Web.Extensions.dll -r:$API/System.Security.dll -r:$API/System.IO.Compression.dll -r:$API/System.IO.Compression.FileSystem.dll \
 src/*.cs
cp app.config out/AgendaGoogleAccessible.exe.config
