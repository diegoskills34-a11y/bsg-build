# v0.35 - Bestiario MMO

Interfaz: tres columnas (familias, criaturas descubiertas, maestrias/titulos).
Nombres para jugadores, no IDs XML ni clases C#. Retratos ilustrativos por familia.
Se cierra al cerrar CharacterFrameWindow (deja de estar visible). No se asignan hotkeys.
Las bajas registradas desde v0.21 son distintas de las cifras historicas de PlayerTitles;
no fingir barras de progreso historico calculadas a partir de ellas.
Los rangos I/II/III entregan 10/20/25 daño y 0/5/10 desmembramiento;
los rangos 4+ son honorificos.
La familia Radiados NO es Tainted. Familias superpuestas: se aplica el bonus mayor.
La ventana de Títulos recibe tooltips de requisito y recompensa
mediante PlayerTitlesTooltipBridge, sin cambiar la seleccion ni la estructura nativa.
Retratos originales representativos, no imágenes reales de las entidades Rebirth:
poner PNGs llamados infected.png, radiated.png, etc. en
0_BSG_Bestiario/UI/Portraits/. Sin archivos se muestra arte de respaldo generativo.
Pendiente prueba de juego: verificar cierre del panel, lectura de caption XUiV_ToggleButton,
carga de PNG y fuentes Unicode/acentos.
