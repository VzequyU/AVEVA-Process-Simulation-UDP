# tanques4 v2: puente TCP sincronizado por paso

Reemplazo directo de la DLL `tanques4` actual (la de `exemplos/tanques4`). Mantiene el mismo
namespace, las mismas 2 ecuaciones y las mismas 7 variables (`Q1, Q2, H1, H2, H3, H4, SimTime`),
así que **el Model Type de AVEVA no cambia**. `SimTime` debe seguir vinculado al reloj
(`M1.SimTime = TG1.TimedValue`).

## Qué cambia respecto a la v1

| v1 (UDP) | v2 (TCP) |
|---|---|
| Envía un paquete en **cada evaluación del residuo** (varias iteraciones de Newton por paso): llegan valores no convergidos y duplicados | Envía **una muestra por paso aceptado**, con el valor convergido |
| El comando se aplica cuando llega, a mitad de un paso | Se aplica en el **borde de un paso** (o al instante con `"apply":"now"`) |
| Sin confirmación | `queued` (recibido) + `ack` (aplicado, con tiempo y motivo) |
| La telemetría no trae las entradas aplicadas | Cada muestra trae `q1, q2` aplicados, consignas, id del comando y nº de evaluaciones |
| 4 decimales (`F4`) | Precisión completa (`R`) |
| UDP, sin control de pérdidas | TCP + número de secuencia; cola sin bloqueo (el solver nunca espera a Python) |
| — | Detecta pasos rechazados y restauraciones de snapshot (`rewind`) |

## Instalación

1. **Cierra AVEVA** (Windows bloquea la DLL cargada).
2. **Respalda la DLL actual fuera de la carpeta de Addons** (si queda dentro, AVEVA puede cargar
   las dos):
   `C:\Program Files\SimSci\SimCentral\SimCentral Addons\tanques4.dll` →
   uma pasta de respaldo, p. ex. `...\tanques4_v1_backup.dll`
3. Compila `tanques4.sln` (Release) y copia `bin\Release\tanques4.dll` a `SimCentral Addons\`.
4. Abre AVEVA y la simulación de los tanques, pasa a **Dynamics** y verifica que no haya cruces rojas.

**Volver a la v1:** cierra AVEVA y copia de vuelta el respaldo.

> La v2 escucha en **TCP 127.0.0.1:5006**. Los scripts de la v1 (UDP) no funcionan con la v2;
> usa `python/aveva_bridge_v2.py`.

## Prueba fuera de línea (sin AVEVA)

`test/FakeSolver.exe` (compila `FakeSolver.cs` con `csc`, referenciando `tanques4.dll` y la DLL de
interfaces del EETK, y copia ambas DLL a `test/`) carga la DLL real y la llama como el solver: 3 iteraciones de Newton por
paso, un paso rechazado y una restauración de snapshot.

```
cd test
python test_offline.py 200      # rápida
python test_offline.py 20000    # carga
```

Resultado obtenido: todas las muestras coinciden exactamente (1e-12) con los valores
convergidos; 0 descartadas; ~75 000–85 000 muestras/s.

## Protocolo (una línea JSON por mensaje)

Python → DLL: `{"cmd":"set","id":7,"q1":0.08,"q2":0.06,"apply":"step"}`, `{"cmd":"flush"}`,
`{"cmd":"ping","id":1}`, `{"cmd":"status"}`, `{"cmd":"reset_seq"}`

DLL → Python: `hello`, `queued`, `ack`, `sample`, `rewind`, `superseded`, `flushed`, `pong`,
`status`, `error` (ver `EquationSet.cs`).
