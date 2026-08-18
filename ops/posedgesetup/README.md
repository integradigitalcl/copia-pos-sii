# Instaladores Grunflex POS / PosEdge

## Multicaja (principal + adicional)

- Script: `single/compile-installer.ps1`
- Salida: `single/out/PosEdge-Setup.exe`
- Wizard: elige **caja principal** o **caja adicional** + IP del servidor

## Mono caja (un solo equipo)

- Script: `monocaja/compile-installer.ps1`
- Salida: `monocaja/out/GrunflexPOS-Mono-Setup.exe`
- Sin pantalla de rol: siempre instala como **caja principal** local

## Ambos a la vez

```powershell
powershell -ExecutionPolicy Bypass -File ops/posedgesetup/compile-all-installers.ps1 -Configuration Release
```

Un solo `build-staging` y dos compilaciones Inno Setup. Manifiesto en `INSTALLERS-Release.txt`.

