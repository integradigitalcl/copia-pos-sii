## Instalación servidor (PC1) — PosEdge Multicaja

### Qué vas a ver

Un instalador normal de Windows:

- **Siguiente**
- **Siguiente**
- **Terminar**

Al finalizar debe decir: **“Servidor multicaja listo”**.

### Pasos

1. En la PC que será **Servidor**, ejecutar:
   - `PosEdge-Server-Setup.exe`
2. Aceptar permisos de administrador si Windows lo solicita.
3. Esperar a que termine la configuración automática.

### Qué configura solo

- Instala **PostgreSQL** si hace falta.
- Prepara la base (`schema.sql` + `seed.sql` del piloto).
- Instala y arranca servicios de Windows:
  - **PosEdgeApi**
  - **PosEdgeWorkers**
- Abre el firewall para que las terminales se conecten.

### Cómo verificar (sin técnico)

Al finalizar, si la pantalla dice **Servidor multicaja listo**, ya está.

Si querés confirmar visualmente:

- Abrí el menú inicio → **PosEdge Multicaja Server** → **Ver estado multicaja**

