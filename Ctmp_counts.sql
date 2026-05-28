SET NOCOUNT ON;
PRINT '=== EXISTING TABLES ===';
SELECT 'Customers' AS T, COUNT(*) AS Rows FROM Customers
UNION ALL SELECT 'Bills', COUNT(*) FROM Bills
UNION ALL SELECT 'Tickets', COUNT(*) FROM Tickets
UNION ALL SELECT 'Outages', COUNT(*) FROM Outages
UNION ALL SELECT 'MeterReadings', COUNT(*) FROM MeterReadings
UNION ALL SELECT 'CsatResponses', COUNT(*) FROM CsatResponses
UNION ALL SELECT 'Tariffs', COUNT(*) FROM Tariffs
UNION ALL SELECT 'Regions', COUNT(*) FROM Regions
UNION ALL SELECT 'Departments', COUNT(*) FROM Departments
UNION ALL SELECT 'ServiceTypes', COUNT(*) FROM ServiceTypes;
PRINT '=== PHASE 06 NEW TABLES ===';
SELECT 'Currencies' AS T, COUNT(*) AS Rows FROM Currencies
UNION ALL SELECT 'PaymentMethods', COUNT(*) FROM PaymentMethods
UNION ALL SELECT 'CustomerSegments', COUNT(*) FROM CustomerSegments
UNION ALL SELECT 'ServicePoints', COUNT(*) FROM ServicePoints
UNION ALL SELECT 'ServiceAccounts', COUNT(*) FROM ServiceAccounts
UNION ALL SELECT 'Payments', COUNT(*) FROM Payments
UNION ALL SELECT 'TariffTiers', COUNT(*) FROM TariffTiers
UNION ALL SELECT 'Subsidies', COUNT(*) FROM Subsidies
UNION ALL SELECT 'Assets', COUNT(*) FROM Assets
UNION ALL SELECT 'Technicians', COUNT(*) FROM Technicians
UNION ALL SELECT 'WorkOrders', COUNT(*) FROM WorkOrders
UNION ALL SELECT 'MaintenanceSchedules', COUNT(*) FROM MaintenanceSchedules
UNION ALL SELECT 'CallLogs', COUNT(*) FROM CallLogs
UNION ALL SELECT 'OutageNotifications', COUNT(*) FROM OutageNotifications
UNION ALL SELECT 'SlaPolicies', COUNT(*) FROM SlaPolicies;
