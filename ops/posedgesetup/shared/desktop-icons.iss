; Accesos directos en el escritorio del usuario que instala (un solo icono principal + carpeta de módulos).
; Requiere #define MyAppName y #define MyLauncher en el .iss principal.

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\Launcher\{#MyLauncher}"; Comment: "Abrir Grunflex POS"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"

Name: "{autodesktop}\{#MyAppName}\Ventas\Ventas"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module ventas"; Comment: "Punto de venta — ventas"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Inventario\Inventario"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module inventario"; Comment: "Ajustes de inventario"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"

Name: "{autodesktop}\{#MyAppName}\Reportes\Panel de control\Panel de control"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reportes"; Comment: "Panel de control y KPIs"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Ventas del día\Ventas del día"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-ventas-del-dia"; Comment: "Historial de ventas del día"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Ventas últimos 7 días\Ventas últimos 7 días"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-ventas-7-dias"; Comment: "Gráfico ventas semanales"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Ventas por hora\Ventas por hora"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-ventas-por-hora"; Comment: "Distribución horaria de ventas"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Métodos de pago\Métodos de pago"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-metodos-pago"; Comment: "Ventas por método de pago"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Top productos\Top productos"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-top-productos"; Comment: "Productos más vendidos"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
Name: "{autodesktop}\{#MyAppName}\Reportes\Estado del negocio\Estado del negocio"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--module reporte-estado-negocio"; Comment: "Resumen y salud del negocio"; IconFilename: "{app}\GrunflexPOS\GrunflexPOS2.exe"
