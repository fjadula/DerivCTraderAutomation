-- Add ScheduledAtUtc column for scheduled signal providers (CMFLIX, etc.)
-- This allows signals to be executed at a specific future time rather than immediately

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ParsedSignalsQueue')
    AND name = 'ScheduledAtUtc'
)
BEGIN
    ALTER TABLE ParsedSignalsQueue ADD ScheduledAtUtc DATETIME NULL;
    PRINT 'Added ScheduledAtUtc column to ParsedSignalsQueue';
END
GO

-- Create filtered index for efficient scheduling queries
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('ParsedSignalsQueue')
    AND name = 'IX_ParsedSignalsQueue_ScheduledAtUtc'
)
BEGIN
    CREATE INDEX IX_ParsedSignalsQueue_ScheduledAtUtc
    ON ParsedSignalsQueue(ScheduledAtUtc)
    WHERE ScheduledAtUtc IS NOT NULL AND Processed = 0;
    PRINT 'Created IX_ParsedSignalsQueue_ScheduledAtUtc index';
END
GO

PRINT 'Migration complete: ScheduledAtUtc column ready for scheduled signal providers';
