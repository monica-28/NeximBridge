# NeximBridge

NeximBridge es un servicio desarrollado en .NET 8 que actúa como puente entre **FUJI Nexim Central Server** y una base de datos **SQL Server**.

Su objetivo es recibir eventos de producción de Nexim mediante TCP, validar y procesar los mensajes según el protocolo FUJI, conservar el mensaje original y almacenar la información relevante en tablas SQL normalizadas para su posterior explotación desde ERP, reporting, Power BI u otros sistemas.

## Arquitectura

```text
FUJI Nexim Central Server
          │
          │ TCP
          ▼
┌──────────────────────────┐
│       NeximBridge        │
│                          │
│ Framing / Parsing        │
│ Validación FUJI          │
│ ACK                      │
│ Deduplicación            │
│ Logging                  │
│ Normalización            │
└────────────┬─────────────┘
             │
             │ SQL
             ▼
┌──────────────────────────┐
│      SQL Server          │
│                          │
│ nexim_raw_event          │
│ nexim_component_event    │
│ nexim_incident_event     │
│ nexim_production_event   │
└──────────────────────────┘
```

El bridge se conecta como host al servidor Nexim, realiza la secuencia de inicialización, se suscribe a los eventos configurados y permanece en estado de recepción.

## Requisitos

- Windows 10/11 o Windows Server.
- .NET 8 SDK para desarrollo.
- SQL Server.
- Acceso TCP al Nexim Central Server.
- Puerto Nexim configurado, normalmente `30040`.
- Acceso a la base de datos utilizada por NeximBridge.

Para comprobar la versión de .NET instalada:

```powershell
dotnet --version
```

## Eventos procesados

Actualmente NeximBridge procesa los siguientes eventos de negocio:

| Evento | Descripción | ACK |
|---|---|---:|
| `PARTSUSAGE` | Consumo y uso de componentes | Sí |
| `PDERROR` | Errores relacionados con montaje/componentes | Sí |
| `PRODSTARTED` | Inicio de producción | Sí |
| `PRODCOMPLETEDII` | Finalización de producción | Sí |
| `PRODSTOPPED` | Parada de producción | Sí |
| `LOADCOMPIV` | Carga de componente/bobina | No |
| `CHANGECOMPII` | Cambio de bobina | No |
| `UNLOADCOMP` | Retirada de componente/bobina | No |
| `MCALARMON` | Activación de alarma de máquina | Sí |
| `MCALARMOFF` | Fin de alarma de máquina | Sí |

Además se gestionan los mensajes técnicos necesarios para mantener la sesión:

```text
SETEV / SETEV_ACK
STARTEV / STARTEV_ACK
KEEPALIVE / KEEPALIVE_ACK
```

### Eventos no utilizados

Actualmente no se solicitan ni procesan otros eventos del catálogo FUJI que no estén configurados explícitamente en `SETEV`.

Entre los eventos estudiados pero no incluidos se encuentran:

```text
PCBCHECKOUTVII
RECOVERYUP
```

El proyecto no pretende actualmente implementar trazabilidad individual de PCB.

## Información obtenida

### Producción

Los eventos de producción permiten obtener:

- Fecha y hora.
- Línea.
- Máquina.
- Módulo.
- Lane.
- Programa.
- Panel.
- Modo de producción.
- Inicio de producción.
- Finalización.
- Parada.
- Número de bloques.
- Bloques omitidos.
- BSInfoBit.
- Tiempo de ciclo.

### Componentes

Los eventos de componentes permiten obtener:

- Componente.
- Stage.
- Slot.
- Feeder.
- Reel.
- Lote.
- Proveedor.
- Date code.
- Cantidad cargada.
- Cantidad restante.
- Tiempo restante.
- Bobina anterior.
- Bobina nueva.
- Consumo de componentes.
- Pickups.
- Rechazos y errores asociados.

`LOADCOMPIV` proporciona la relación entre:

```text
Machine + Module + Stage + Slot
                │
                ▼
             ReelID
                │
                ▼
             LotNo
```

Esto permite posteriormente relacionar el consumo comunicado mediante `PARTSUSAGE` con la bobina y lote que estaban cargados en esa posición.

### Incidencias

Se registran:

- Alarmas de máquina.
- Inicio y fin de alarma.
- Código y subcódigo.
- Errores de montaje.
- Componente.
- Slot.
- AVL.
- Cabezal.
- Holder.
- Nozzle.
- Nozzle pit.
- Board.
- Reference.

## Base de datos

El esquema completo se encuentra versionado en:

```text
database/NeximBridge_schema.sql
```

El esquema puede utilizarse para recrear las tablas necesarias en otra instancia SQL Server.

### `nexim_raw_event`

Contiene todos los mensajes recibidos antes de normalizarlos.

Campos principales:

```text
raw_event_id
received_at
event_name
seq_id
event_time_utc
line_name
machine_name
raw_message
parse_status
ack_status
error_message
raw_hash
```

El mensaje original se conserva en `raw_message`.

Estados habituales:

```text
PENDING
PROCESSED
ERROR
```

`raw_hash` se utiliza para detectar retransmisiones duplicadas.

### `nexim_component_event`

Contiene los datos procedentes de:

```text
PARTSUSAGE
LOADCOMPIV
CHANGECOMPII
UNLOADCOMP
```

Incluye información sobre:

```text
máquina
módulo
lane
programa
panel
stage
slot
componente
AVL
feeder
reel
old reel
new reel
lote
proveedor
cantidad
consumo
pickups
errores
rechazos
lighting class
supply method
remaining time
```

### `nexim_incident_event`

Contiene:

```text
PDERROR
MCALARMON
MCALARMOFF
```

Incluye información de error, posición, componente, nozzle y códigos de alarma.

### `nexim_production_event`

Contiene:

```text
PRODSTARTED
PRODCOMPLETEDII
PRODSTOPPED
```

Permite reconstruir el flujo y los tiempos de producción.

## Relación entre RAW y datos normalizados

`raw_event_id` permite relacionar cada mensaje original con los registros normalizados que ha generado.

Por ejemplo:

```text
nexim_raw_event
raw_event_id = 500
event_name   = PARTSUSAGE

            │
            ▼

nexim_component_event

500 | Slot 5 | RES-10K  | 100 uds
500 | Slot 6 | CAP-100N | 50 uds
```

Un único mensaje FUJI puede generar múltiples registros normalizados.

## Deduplicación

Los eventos de negocio utilizan un hash SHA-256 calculado sobre el mensaje RAW exacto recibido.

Si se recibe de nuevo exactamente el mismo mensaje:

- No se genera un segundo registro normalizado.
- Se reutiliza el evento RAW ya existente.
- Si el evento requiere ACK y ya había sido procesado correctamente, se responde nuevamente con `Result=0`.
- Los eventos configurados con `Ack=false` continúan sin generar ACK.

La deduplicación se apoya también en un índice único SQL sobre `raw_hash`.

## KEEPALIVE

NeximBridge responde a los mensajes:

```text
KEEPALIVE<TAB>SeqID
```

mediante:

```text
KEEPALIVE_ACK<TAB>SeqID
```

El ACK se envía antes de intentar persistir información en SQL, evitando que un problema de base de datos provoque la pérdida de la sesión Nexim.

## Reconexión TCP

Si Nexim cierra la conexión o temporalmente no está disponible, NeximBridge intenta reconectar automáticamente.

El backoff utilizado es:

```text
1 s
2 s
5 s
10 s
30 s
```

Después de reconectar se vuelve a ejecutar:

```text
SETEV
SETEV_ACK
STARTEV
STARTEV_ACK
```

antes de volver al estado de recepción.

## Configuración

La configuración principal se encuentra en:

```text
appsettings.json
```

Ejemplo:

```json
{
  "Nexim": {
    "Host": "127.0.0.1",
    "Port": 30040,
    "MachineName": "",
    "InitializationTimeoutSeconds": 10,

    "Events": [
      { "Name": "PARTSUSAGE", "Ack": true },
      { "Name": "PDERROR", "Ack": true },
      { "Name": "PRODSTARTED", "Ack": true },
      { "Name": "PRODCOMPLETEDII", "Ack": true },
      { "Name": "PRODSTOPPED", "Ack": true },
      { "Name": "LOADCOMPIV", "Ack": false },
      { "Name": "CHANGECOMPII", "Ack": false },
      { "Name": "UNLOADCOMP", "Ack": false },
      { "Name": "MCALARMON", "Ack": true },
      { "Name": "MCALARMOFF", "Ack": true }
    ],

    "SQL": {
      "ConnectionString": "..."
    }
  }
}
```

## Credenciales SQL

No deben almacenarse contraseñas reales en el `appsettings.json` versionado en Git.

Para desarrollo puede utilizarse una variable de entorno de PowerShell:

```powershell
$env:Nexim__SQL__ConnectionString = "Server=SERVIDOR;Database=NeximBridgeBD;User Id=USUARIO;Password=CLAVE;Encrypt=True;TrustServerCertificate=True;"
```

Después puede ejecutarse normalmente:

```powershell
dotnet run --project .\NeximBridge.csproj
```

La variable sólo se aplica a esa sesión de PowerShell.

## Compilación

Desde la raíz del repositorio:

```powershell
dotnet restore
dotnet build .\NeximBridge.csproj
dotnet build .\NeximSimulator\NeximSimulator.csproj
```

## Tests

Ejecutar:

```powershell
dotnet run --project .\tests\NeximBridge.ProtocolTests

dotnet run --project .\tests\NeximBridge.PartsUsageTests

dotnet run --project .\tests\NeximBridge.LoggingTests
```

Los tests cubren, entre otros:

- Framing TCP.
- STX / ETX.
- Longitud Big Endian.
- Inicialización.
- Parseo de eventos.
- ACK.
- KEEPALIVE.
- Rangos FUJI.
- Deduplicación.
- Persistencia RAW.
- Logging.
- Reconexión.

## Prueba con NeximSimulator

Abrir dos terminales.

### Terminal 1

Desde la raíz:

```powershell
$env:Nexim__SQL__ConnectionString = "Server=SERVIDOR;Database=NeximBridgeBD;User Id=USUARIO;Password=CLAVE;Encrypt=True;TrustServerCertificate=True;"

dotnet run --project .\NeximBridge.csproj
```

### Terminal 2

```powershell
dotnet run --project .\NeximSimulator\NeximSimulator.csproj
```

El simulador envía una secuencia de eventos similar a:

```text
PARTSUSAGE
PDERROR
PRODSTARTED
PRODCOMPLETEDII
PRODSTOPPED
LOADCOMPIV
CHANGECOMPII
UNLOADCOMP
MCALARMON
MCALARMOFF
KEEPALIVE
```

Los eventos configurados con ACK deben devolver `Result=0`.

## Comprobar eventos procesados

En SQL Server:

```sql
SELECT TOP 30
    raw_event_id,
    received_at,
    event_name,
    seq_id,
    parse_status,
    ack_status,
    error_message
FROM dbo.nexim_raw_event
ORDER BY raw_event_id DESC;
```

Resumen por tipo:

```sql
SELECT
    event_name,
    parse_status,
    COUNT(*) AS cantidad
FROM dbo.nexim_raw_event
GROUP BY
    event_name,
    parse_status
ORDER BY
    event_name,
    parse_status;
```

Eventos con error:

```sql
SELECT
    raw_event_id,
    received_at,
    event_name,
    seq_id,
    error_message
FROM dbo.nexim_raw_event
WHERE parse_status = 'ERROR'
ORDER BY raw_event_id DESC;
```

## Logging

NeximBridge escribe logs persistentes en archivo mediante Serilog.

Configuración habitual:

```json
"NeximLogging": {
  "FilePath": "logs/neximbridge-.log",
  "RetainedFileCountLimit": 30,
  "FileSizeLimitBytes": 20971520
}
```

Los logs incluyen:

- Inicio y parada.
- Conexiones TCP.
- Reconexiones.
- SETEV / STARTEV.
- Eventos procesados.
- ACK.
- Duplicados.
- KEEPALIVE.
- Errores SQL o de protocolo.

Los mensajes RAW completos no se escriben normalmente a nivel `Information`.

## Publicación Release

Para generar un ejecutable self-contained de Windows x64:

```powershell
dotnet publish .\NeximBridge.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o .\publish
```

El resultado puede ejecutarse con:

```powershell
.\publish\NeximBridge.exe
```

## Windows Service

El proyecto utiliza:

```text
Microsoft.Extensions.Hosting.WindowsServices
```

y está preparado para ejecutarse como servicio de Windows.

Para producción se recomienda desplegar el resultado del `publish` en una ubicación estable, por ejemplo:

```text
C:\Services\NeximBridge
```

No se recomienda ejecutar el servicio directamente desde:

```text
OneDrive
carpetas de usuario
carpetas de desarrollo
bin/Debug
```

## Logs en producción

Se recomienda utilizar una ubicación como:

```text
C:\ProgramData\EPSA\NeximBridge\logs
```

en lugar de almacenar logs junto al código fuente.

## Seguridad

No versionar:

```text
contraseñas
connection strings reales con credenciales
*.bak
*.mdf
*.ldf
logs
bin
obj
publish
```

El repositorio debe contener el esquema SQL:

```text
database/NeximBridge_schema.sql
```

pero no la base de datos física ni datos de producción.

## Git

Los artefactos generados por compilación están excluidos mediante `.gitignore`.

Antes de hacer commit:

```powershell
git status
```

Nunca añadir automáticamente archivos sensibles sin revisar previamente:

```powershell
git diff
git status
```

## Documentación FUJI

La implementación se ha desarrollado tomando como referencia:

```text
FUJI CORPORATION
Nexim Host Interface Specification
V2.2.0
No. Nexim-1283
```

Antes de añadir nuevos eventos o modificar la estructura de los existentes debe comprobarse siempre su definición en la documentación oficial FUJI.

No deben añadirse campos, rangos o ACK basándose únicamente en suposiciones.

## Estado actual

Actualmente están implementados y probados:

```text
Framing TCP                       OK
SETEV / STARTEV                  OK
KEEPALIVE                        OK
Eventos configurados             OK
ACK                              OK
Persistencia RAW                 OK
Normalización SQL                OK
Deduplicación                    OK
Reconexión automática            OK
Logging persistente              OK
Simulador                        OK
Tests automáticos                OK
Publish Release                  OK
```

## Objetivo de integración

Los datos almacenados permiten posteriormente construir información útil para otros sistemas:

```text
Producto / programa
        │
        ├── tiempos de fabricación
        ├── paneles producidos
        ├── componentes consumidos
        ├── reels utilizados
        ├── lotes utilizados
        ├── cambios de bobina
        ├── errores de montaje
        └── alarmas de máquina
```

Esto permite utilizar NeximBridge como capa de integración entre FUJI Nexim y ERP, sistemas de reporting o herramientas de análisis sin acoplar directamente dichos sistemas al protocolo propietario de Nexim.
