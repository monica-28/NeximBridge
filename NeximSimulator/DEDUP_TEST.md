# Prueba de retransmisión exacta

Desde la raíz del proyecto:

```powershell
dotnet run --project .\NeximSimulator\NeximSimulator.csproj -- --dedup-test
```

El simulador escucha en 127.0.0.1:30040 y espera la conexión de NeximBridge.
Después del SETEV/STARTEV habitual exige PARTSUSAGE con ACK=1 y LOADCOMPIV
con ACK=0. No requiere los demás eventos para este modo.

Envía PARTSUSAGE SeqID=2001, ProgramName=DEDUP_TEST, PanelNo=DEDUP001 y dos
detalles. Valida los ocho campos del ACK con Result=0 y vuelve a escribir el
mismo buffer de frame. Valida de nuevo el ACK antes de continuar.

Envía LOADCOMPIV SeqID=2002 y dos detalles, espera 500 ms y escribe de nuevo
el mismo buffer. No espera ningún ACK de LOADCOMPIV. Tras una pausa de un
segundo, cierra la conexión y termina. No ejecuta otros escenarios ni KEEPALIVE.
Cada evento genera su Time una sola vez. No hay deduplicación en el simulador:
se realizan cuatro escrituras de frames de evento.

Una suscripción incorrecta o un ACK inválido/ausente hace terminar la prueba
con código de salida 1. El timeout de cada PARTSUSAGE_ACK es de 20 segundos.
Sin `--dedup-test` se conserva la secuencia normal y la sesión KEEPALIVE existente.

## Pruebas rápidas

Con el puerto 30040 libre, ejecutar secuencialmente:

```powershell
dotnet build .\NeximSimulator\NeximSimulator.csproj
python .\NeximSimulator\tests\test_dedup_mode.py
python .\NeximSimulator\tests\test_remaining_events.py
dotnet run --project .\tests\NeximBridge.ProtocolTests
```

Las pruebas TCP utilizan un cliente de prueba y sólo arrancan NeximSimulator.
Comprueban los bytes completos, los timestamps, los datos de los registros,
los ACK, las pausas, el cierre y la secuencia normal sin el flag.
No arrancan NeximBridge ni consultan/escriben SQL. La comprobación de que sólo
existe un RAW y un lote normalizado por evento corresponde a una ejecución
contra el NeximBridge local; recibir ACK=0 por sí solo no demuestra el número
de filas almacenadas.
