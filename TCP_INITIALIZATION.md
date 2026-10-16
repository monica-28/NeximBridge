# Inicialización TCP local

Referencia: Nexim Host System Interface Specification V2.2.0 or later,
apartados 4.1 (p. 32), 4.8 (pp. 42–44) y 4.9 (p. 45).

Cada conexión sigue `TCP → SETEV → SETEV_ACK → STARTEV → STARTEV_ACK → recepción`.
Worker usa NeximSessionInitializer, que invoca ambos builders. Una reconexión repite
el intercambio completo. No se entregan mensajes a los handlers antes de confirmarlo.

## Formato

Los ejemplos representan el cuerpo entre STX y ETX; `\t` significa TAB y `\r` CR.
La cabecera binaria de cuatro bytes big endian contiene el número de bytes de ese
cuerpo más STX y ETX, excluyendo la propia cabecera.

```text
SETEV\t1\t\t2\rPARTSUSAGE\t1\rPDERROR\t1\r
SETEV_ACK\t1\t\t0
STARTEV\t2\t
STARTEV_ACK\t2\t\t0
```

MachineName ocupa siempre su campo. Vacío significa todas las máquinas. NumList
es el número decimal de registros, sin la etiqueta literal «NumList». Cada registro
termina en CR, también el último. SeqID avanza entre 1 y 999999 y vuelve a 1.
Ambos ACK deben tener exactamente cuatro campos y coincidir en comando, SeqID
y MachineName, con Result=0. Result=1, otros valores, campos incorrectos,
desconexión, framing inválido o timeout impiden pasar a recepción y cierran la sesión.

## Configuración

En `appsettings.json`, `Nexim:Host=127.0.0.1` y `Nexim:Port=30040` apuntan al
simulador. `Nexim:Events` contiene objetos `{ "Name": "PARTSUSAGE", "Ack": true }`.
Cada suscripción genera `Name<TAB>1<CR>` o `Name<TAB>0<CR>` según `Ack`.
LOADCOMPIV, CHANGECOMPII y UNLOADCOMP utilizan `Ack=false`; los cinco eventos
anteriores y MCALARMON/MCALARMOFF utilizan `Ack=true`.
`Nexim:MachineName` está vacío por defecto.
`Nexim:InitializationTimeoutSeconds=10` limita cada intercambio de inicialización,
incluida la lectura de respuestas parciales. Es una política local, no un timeout
prescrito por Fuji. El lector limita localmente los frames a 16 MiB.

El simulador valida orden, estructura, SeqID, MachineName, NumList, registros,
duplicados, nombres admitidos y valores ACK 0/1. Conserva las suscripciones y
devuelve Result=1 ante solicitudes identificables inválidas. Si no puede identificar
comando, SeqID y MachineName, cierra sin inventar un ACK.

Admite las suscripciones PARTSUSAGE, PDERROR, PRODSTARTED, PRODCOMPLETEDII,
PRODSTOPPED, LOADCOMPIV, CHANGECOMPII, UNLOADCOMP, MCALARMON, MCALARMOFF,
RECOVERYUP y KEEPALIVE. Los demás nombres se rechazan con Result=1; es el
subconjunto admitido por este simulador, no el catálogo completo de Fuji.

Después de STARTEV envía los diez escenarios descritos en [REMAINING_EVENTS.md](REMAINING_EVENTS.md),
con SeqID 1001–1010. A continuación mantiene la conexión con KEEPALIVE desde
SeqID=1011: intervalo de 120 segundos y timeout de 30 segundos para su ACK.
Acepta nuevas conexiones tras finalizar cada sesión. RECOVERYUP no se genera ni
se procesa en NeximBridge, aunque el validador de SETEV lo reconoce.

## Pruebas desde PowerShell

Requisito: SDK .NET 8. Desde la raíz del proyecto:

```powershell
dotnet run --project .\tests\NeximBridge.ProtocolTests\NeximBridge.ProtocolTests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Han fallado las pruebas de protocolo' }
dotnet build .\NeximBridge.csproj
dotnet build .\NeximSimulator\NeximSimulator.csproj
```

El proyecto de pruebas es un ejecutable autónomo sin paquetes de testing;
se ejecuta con `dotnet run`, no con `dotnet test`. Devuelve 0 si todo pasa y 1
si falla algún caso. Las 63 pruebas incluyen mensajes de referencia independientes,
framing fragmentado/concatenado, UTF-8, longitudes y delimitadores inválidos,
inicialización TCP, gating del simulador, reconexión, Result=1, SeqID incorrecto,
MachineName incorrecto/ausente, desconexión, cancelación y timeout en ambas fases.
Usan exclusivamente loopback con puertos efímeros; no inician Worker ni acceden a SQL.

Para iniciar manualmente el simulador y Bridge, en dos terminales separadas:

```powershell
# Terminal 1
dotnet run --project .\NeximSimulator\NeximSimulator.csproj

# Terminal 2
dotnet run --project .\NeximBridge.csproj -- --Nexim:Host=127.0.0.1 --Nexim:Port=30040
```

Ambos registran la finalización de SETEV/STARTEV. Esta comprobación solo requiere
la inicialización; detener con Ctrl+C después del mensaje de recepción evita llegar
al KEEPALIVE periódico, cuyo handler existente utiliza SQL. Para verificar sin SQL,
usar el proyecto de pruebas anterior.

## Archivos

- `appsettings.json`: puerto, eventos, máquina y timeout.
- `NeximSetEventBuilder.cs`, `NeximStartEventBuilder.cs`: mensajes oficiales.
- `NeximInitializationProtocol.cs`: framing de salida y validación de inicialización.
- `NeximSessionInitializer.cs`: intercambio secuencial, estados, timeout y cierre.
- `Worker.cs`, `Program.cs`: ejecución y registro de la inicialización.
- `NeximTcpClientService.cs`: desconexión condicionada al stream para evitar que
  un error tardío cierre una conexión nueva.
- `NeximFrameReader.cs`: lectura desde Stream para pruebas y límite de tamaño.
- `NeximSimulator/SimulatorInitialization.cs`, `NeximSimulator/Program.cs`:
  validación, ACK oficiales y envío condicionado a la inicialización.
- Ambos `.csproj`: inclusión de protocolo compartido y exclusión de fuentes de
  pruebas/simulador y archivos temporales del proyecto Bridge.
- `tests/NeximBridge.ProtocolTests/`: ejecutable de pruebas de protocolo.
- `TCP_INITIALIZATION.md`: referencia, alcance y comandos.
