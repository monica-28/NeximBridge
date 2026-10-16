-- Revisar y ejecutar manualmente en la base de datos NeximBridge.
-- La aplicación nunca ejecuta este script.
IF COL_LENGTH('dbo.nexim_component_event', 'lighting_class') IS NULL
    ALTER TABLE dbo.nexim_component_event ADD lighting_class VARCHAR(100) NULL;
GO
IF COL_LENGTH('dbo.nexim_component_event', 'supply_method') IS NULL
    ALTER TABLE dbo.nexim_component_event ADD supply_method INT NULL;
GO
IF COL_LENGTH('dbo.nexim_component_event', 'remaining_time') IS NULL
    ALTER TABLE dbo.nexim_component_event ADD remaining_time INT NULL;
GO
