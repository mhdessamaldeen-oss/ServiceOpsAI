-- ─────────────────────────────────────────────────────────────────────────────
-- Supplemental seed — 2026-05-28
-- Brings the operational schema up to "questionable today" state:
--   • Tickets created today / this week / this month / Q1-Q2 / etc.
--   • Customers signed up this month
--   • Active outages (EndedAt = NULL)
--   • Ticket comments (was 0 rows; now ~100)
--   • Parent-child ticket relationships (was 0; now 10 chains)
--   • Tickets with assignees + some explicitly unassigned (for IS NULL tests)
--   • Edge-case rows: customer without email, bill without payment date
--
-- Idempotent — uses INSERT … WHERE NOT EXISTS / UPDATE … WHERE clauses keyed
-- on stable predicates. Safe to re-run; will only insert what's missing.
-- ─────────────────────────────────────────────────────────────────────────────

SET NOCOUNT ON;
DECLARE @adminId NVARCHAR(450) = (SELECT TOP 1 Id FROM AspNetUsers WHERE UserName='admin@tech.local');
DECLARE @agent1   NVARCHAR(450) = (SELECT TOP 1 Id FROM AspNetUsers WHERE UserName='agent1@tech.local');
DECLARE @agent2   NVARCHAR(450) = (SELECT TOP 1 Id FROM AspNetUsers WHERE UserName='agent2@tech.local');
DECLARE @user1    NVARCHAR(450) = (SELECT TOP 1 Id FROM AspNetUsers WHERE UserName='user1@interior.local');
DECLARE @today    DATETIME2(7)  = CAST(GETDATE() AS DATE);                -- 2026-05-28 00:00
DECLARE @yesterday DATETIME2(7) = DATEADD(DAY, -1, @today);
DECLARE @threeDaysAgo DATETIME2(7) = DATEADD(DAY, -3, @today);
DECLARE @oneWeekAgo  DATETIME2(7) = DATEADD(DAY, -7, @today);
DECLARE @twoWeeksAgo DATETIME2(7) = DATEADD(DAY, -14, @today);
DECLARE @maxTicketId INT = (SELECT ISNULL(MAX(Id), 0) FROM Tickets);
DECLARE @nextTicketSeq INT = (SELECT ISNULL(MAX(CAST(SUBSTRING(TicketNumber, 5, 10) AS INT)), 0) FROM Tickets WHERE TicketNumber LIKE 'TKT-%');

-- Reference IDs for inserts
DECLARE @damascusId INT  = (SELECT Id FROM Regions WHERE NameEn='Damascus');
DECLARE @aleppoId   INT  = (SELECT Id FROM Regions WHERE NameEn='Aleppo');
DECLARE @homsId     INT  = (SELECT Id FROM Regions WHERE NameEn='Homs');
DECLARE @lattakiaId INT  = (SELECT Id FROM Regions WHERE NameEn='Lattakia');
DECLARE @waterSvc   INT  = (SELECT Id FROM ServiceTypes WHERE Code='Water');
DECLARE @electricSvc INT = (SELECT Id FROM ServiceTypes WHERE Code='Electricity');
DECLARE @gasSvc     INT  = (SELECT Id FROM ServiceTypes WHERE Code='Gas');
DECLARE @internetSvc INT = (SELECT Id FROM ServiceTypes WHERE Code='Internet');
DECLARE @newStatus  INT = (SELECT Id FROM TicketStatuses WHERE Name='New');
DECLARE @openStatus INT = (SELECT Id FROM TicketStatuses WHERE Name='Open');
DECLARE @inProgress INT = (SELECT Id FROM TicketStatuses WHERE Name='In Progress');
DECLARE @resolvedSt INT = (SELECT Id FROM TicketStatuses WHERE Name='Resolved');
DECLARE @closedSt   INT = (SELECT Id FROM TicketStatuses WHERE Name='Closed');
DECLARE @critPri    INT = (SELECT Id FROM TicketPriorities WHERE Name='Critical');
DECLARE @highPri    INT = (SELECT Id FROM TicketPriorities WHERE Name='High');
DECLARE @medPri     INT = (SELECT Id FROM TicketPriorities WHERE Name='Medium');
DECLARE @webSource  INT = (SELECT Id FROM TicketSources WHERE Name='Web Portal');
DECLARE @phoneSource INT = (SELECT Id FROM TicketSources WHERE Name='Phone');
DECLARE @elecCat    INT = (SELECT TOP 1 Id FROM TicketCategories WHERE Name='Electricity outage' ORDER BY Id);
DECLARE @waterCat   INT = (SELECT TOP 1 Id FROM TicketCategories WHERE Name='Water cut' ORDER BY Id);
DECLARE @gasCat     INT = (SELECT TOP 1 Id FROM TicketCategories WHERE Name='Gas service issue' ORDER BY Id);
DECLARE @internetCat INT = (SELECT TOP 1 Id FROM TicketCategories WHERE Name='Internet outage' ORDER BY Id);
DECLARE @firstCustomer INT = (SELECT MIN(Id) FROM Customers);
DECLARE @secondCustomer INT = (SELECT MIN(Id) FROM Customers WHERE Id > @firstCustomer);
DECLARE @thirdCustomer  INT = (SELECT MIN(Id) FROM Customers WHERE Id > @secondCustomer);

PRINT '── Sanity check: required lookup IDs';
PRINT 'Damascus=' + COALESCE(CAST(@damascusId AS VARCHAR), 'NULL') +
      ' Aleppo=' + COALESCE(CAST(@aleppoId AS VARCHAR), 'NULL') +
      ' Water=' + COALESCE(CAST(@waterSvc AS VARCHAR), 'NULL') +
      ' Open=' + COALESCE(CAST(@openStatus AS VARCHAR), 'NULL') +
      ' Critical=' + COALESCE(CAST(@critPri AS VARCHAR), 'NULL');
IF @damascusId IS NULL OR @waterSvc IS NULL OR @openStatus IS NULL OR @critPri IS NULL OR @adminId IS NULL
BEGIN
    PRINT '!! Missing required lookup IDs; aborting.';
    RETURN;
END

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. RECENT TICKETS — covers "today", "this week", "this month" questions
-- Marker in Title field so this seed is idempotent on re-run.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM Tickets WHERE Title LIKE 'SEED-2026-05-28:%')
BEGIN
    PRINT '── Inserting 30 recent tickets ...';
    DECLARE @i INT = 0;
    WHILE @i < 30
    BEGIN
        SET @nextTicketSeq = @nextTicketSeq + 1;
        DECLARE @tn NVARCHAR(20) = 'TKT-' + RIGHT('00000' + CAST(@nextTicketSeq AS VARCHAR), 5);
        DECLARE @createdAt DATETIME2(7) =
            CASE
                WHEN @i < 6  THEN DATEADD(HOUR, -@i*4, @today)              -- today (6 within hours)
                WHEN @i < 12 THEN DATEADD(DAY, -(@i-5), @today)              -- this week
                WHEN @i < 20 THEN DATEADD(DAY, -(@i-2), @today)              -- this month
                ELSE DATEADD(DAY, -(@i*2), @today)                            -- earlier this month
            END;
        DECLARE @reg INT = CASE (@i % 4) WHEN 0 THEN @damascusId WHEN 1 THEN @aleppoId WHEN 2 THEN @homsId ELSE @lattakiaId END;
        DECLARE @svc INT = CASE (@i % 4) WHEN 0 THEN @electricSvc WHEN 1 THEN @waterSvc WHEN 2 THEN @gasSvc ELSE @internetSvc END;
        DECLARE @cat INT = CASE (@i % 4) WHEN 0 THEN @elecCat WHEN 1 THEN @waterCat WHEN 2 THEN @gasCat ELSE @internetCat END;
        DECLARE @pri INT = CASE (@i % 5) WHEN 0 THEN @critPri WHEN 1 THEN @highPri ELSE @medPri END;
        DECLARE @sts INT = CASE (@i % 7) WHEN 0 THEN @openStatus WHEN 1 THEN @inProgress WHEN 2 THEN @newStatus WHEN 3 THEN @resolvedSt ELSE @openStatus END;
        DECLARE @cust INT = CASE (@i % 3) WHEN 0 THEN @firstCustomer WHEN 1 THEN @secondCustomer ELSE @thirdCustomer END;
        DECLARE @assignee NVARCHAR(450) = CASE WHEN (@i % 3) = 0 THEN NULL ELSE COALESCE(@agent1, @adminId) END;
        DECLARE @resolvedAt DATETIME2(7) = CASE WHEN @sts = @resolvedSt THEN DATEADD(HOUR, 6, @createdAt) ELSE NULL END;

        INSERT INTO Tickets (TicketNumber, Title, Description, CategoryId, PriorityId, StatusId, SourceId, RegionId, CustomerId, AssignedToUserId, CreatedByUserId, CreatedAt, UpdatedAt, DueDate, ResolvedAt, IsDeleted, IsSlaBreached, RequiresManagerReview, AffectedUsersCount)
        VALUES (@tn, 'SEED-2026-05-28: recent ticket ' + CAST(@i AS VARCHAR), 'Auto-seeded recent ticket for shape-suite testing.', @cat, @pri, @sts, @webSource, @reg, @cust, @assignee, @adminId, @createdAt, @createdAt, DATEADD(DAY, 7, @createdAt), @resolvedAt, 0, 0, 0, 1 + (@i % 50));
        SET @i = @i + 1;
    END
    PRINT '── 30 recent tickets inserted.';
END
ELSE PRINT '── Recent tickets already seeded (idempotent skip).';

-- ─────────────────────────────────────────────────────────────────────────────
-- 2. ACTIVE OUTAGES (EndedAt NULL) — was 0; need at least 5
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM Outages WHERE OutageNumber LIKE 'OUT-2026-05-9%' AND EndedAt IS NULL)
BEGIN
    PRINT '── Inserting 5 active outages ...';
    INSERT INTO Outages (OutageNumber, RegionId, ServiceTypeId, StartedAt, EndedAt, Severity, Cause, IsPlanned, AffectedCustomerCount, TitleEn, TitleAr)
    SELECT 'OUT-2026-05-901', @damascusId, @electricSvc, DATEADD(HOUR, -6, @today),  NULL, 'High', 'Transformer failure', 0, 1200, 'Electricity outage in Damascus', 'انقطاع كهرباء في دمشق'
    UNION ALL SELECT 'OUT-2026-05-902', @aleppoId, @waterSvc,    DATEADD(HOUR, -12, @today), NULL, 'Medium', 'Pipe burst', 0, 450, 'Water outage in Aleppo', 'انقطاع مياه في حلب'
    UNION ALL SELECT 'OUT-2026-05-903', @homsId,   @gasSvc,      DATEADD(DAY, -1, @today), NULL, 'Low', 'Maintenance', 1, 80, 'Planned gas maintenance in Homs', 'صيانة غاز مخططة في حمص'
    UNION ALL SELECT 'OUT-2026-05-904', @damascusId, @internetSvc, DATEADD(HOUR, -3, @today), NULL, 'Critical', 'Fiber cut', 0, 5000, 'Internet outage in Damascus', 'انقطاع إنترنت في دمشق'
    UNION ALL SELECT 'OUT-2026-05-905', @lattakiaId, @waterSvc,  DATEADD(HOUR, -2, @today), NULL, 'High', 'Pump failure', 0, 220, 'Water outage in Lattakia', 'انقطاع مياه في اللاذقية';
    PRINT '── 5 active outages inserted.';
END
ELSE PRINT '── Active outages already seeded.';

-- ─────────────────────────────────────────────────────────────────────────────
-- 3. TICKET COMMENTS — was 0; seed ~100 across existing tickets
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM TicketComments WHERE Content LIKE 'SEED-2026-05-28:%')
BEGIN
    PRINT '── Inserting ~100 comments across tickets ...';
    -- Add 1-3 comments to first 50 tickets (leaves the rest commentless for B-NEX tests)
    INSERT INTO TicketComments (TicketId, Content, CreatedByUserId, CreatedAt)
    SELECT t.Id,
           'SEED-2026-05-28: agent comment #' + CAST(ROW_NUMBER() OVER (PARTITION BY t.Id ORDER BY t.Id) AS VARCHAR),
           @adminId,
           DATEADD(HOUR, ROW_NUMBER() OVER (ORDER BY t.Id) * 2, t.CreatedAt)
    FROM (SELECT TOP 50 Id, CreatedAt FROM Tickets WHERE IsDeleted=0 ORDER BY Id) t
    CROSS JOIN (VALUES (1),(2),(3)) AS c(n)
    WHERE (t.Id + c.n) % 4 <> 0;  -- skips some so distribution isn't uniform
    PRINT '── Comments inserted.';
END
ELSE PRINT '── Comments already seeded.';

-- ─────────────────────────────────────────────────────────────────────────────
-- 4. PARENT-CHILD ticket relationships — was 0; need ~10 for recursive CTE tests
-- Pattern: TKT-00010 has children TKT-00020/30/40; TKT-00050 has TKT-00060.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM Tickets WHERE ParentTicketId IS NOT NULL)
BEGIN
    PRINT '── Wiring parent-child ticket relationships ...';
    DECLARE @parent10 INT = (SELECT Id FROM Tickets WHERE TicketNumber='TKT-00010');
    DECLARE @parent50 INT = (SELECT Id FROM Tickets WHERE TicketNumber='TKT-00050');
    DECLARE @parent20 INT = (SELECT Id FROM Tickets WHERE TicketNumber='TKT-00020');
    IF @parent10 IS NOT NULL AND @parent50 IS NOT NULL
    BEGIN
        UPDATE Tickets SET ParentTicketId=@parent10 WHERE TicketNumber IN ('TKT-00020','TKT-00030','TKT-00040');
        UPDATE Tickets SET ParentTicketId=@parent50 WHERE TicketNumber IN ('TKT-00060','TKT-00070');
        UPDATE Tickets SET ParentTicketId=@parent20 WHERE TicketNumber IN ('TKT-00080','TKT-00081','TKT-00082'); -- grandchild chain through 20
        PRINT '── Parent-child wired.';
    END
END
ELSE PRINT '── Parent-child already wired.';

-- ─────────────────────────────────────────────────────────────────────────────
-- 5. RECENT CUSTOMERS (signed up this month) — for "customers signed up this month"
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM Customers WHERE FullNameEn LIKE 'SEED-2026-05-28%')
BEGIN
    PRINT '── Inserting 5 recent customers ...';
    INSERT INTO Customers (NationalId, FullNameEn, FullNameAr, Email, Phone, AddressLineEn, RegionId, Status, SignupAt)
    SELECT 'NID-202605280001','SEED-2026-05-28 Ahmed Al-Hassan','أحمد الحسن','ahmed.h2026@example.sy','+963944111111','Mezzeh, Damascus',@damascusId,'Active',DATEADD(DAY, -2, @today)
    UNION ALL SELECT 'NID-202605280002','SEED-2026-05-28 Fatima Al-Khalid','فاطمة الخالد','fatima.k2026@example.sy','+963944222222','Sulaymaniyah, Aleppo',@aleppoId,'Active',DATEADD(DAY, -5, @today)
    UNION ALL SELECT 'NID-202605280003','SEED-2026-05-28 Omar Sayyid','عمر السيد',NULL,'+963944333333','Old Homs, Homs',@homsId,'Active',DATEADD(DAY, -10, @today)  -- no email (for IS NULL tests)
    UNION ALL SELECT 'NID-202605280004','SEED-2026-05-28 Layla Saeed','ليلى السعيد','layla.s2026@example.sy','+963944444444','Banias, Tartus',(SELECT Id FROM Regions WHERE NameEn='Tartus'),'Active',DATEADD(DAY, -1, @today)
    UNION ALL SELECT 'NID-202605280005','SEED-2026-05-28 Hassan Al-Najjar','حسن النجار','hassan.n2026@example.sy','+963944555555','Idlib Center, Idlib',(SELECT Id FROM Regions WHERE NameEn='Idlib'),'Active',@today;
    PRINT '── Customers inserted (1 with NULL email).';
END
ELSE PRINT '── Recent customers already seeded.';

-- ─────────────────────────────────────────────────────────────────────────────
-- 6. UNRESOLVED OVERDUE TICKETS — DueDate < today, ResolvedAt NULL — for overdue concept
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM Tickets WHERE Title LIKE 'SEED-OVERDUE-2026-05-28:%')
BEGIN
    PRINT '── Inserting 8 overdue-unresolved tickets ...';
    SET @i = 0;
    WHILE @i < 8
    BEGIN
        SET @nextTicketSeq = @nextTicketSeq + 1;
        SET @tn = 'TKT-' + RIGHT('00000' + CAST(@nextTicketSeq AS VARCHAR), 5);
        DECLARE @overdueCreatedAt DATETIME2(7) = DATEADD(DAY, -20 - @i, @today);
        INSERT INTO Tickets (TicketNumber, Title, Description, CategoryId, PriorityId, StatusId, SourceId, RegionId, CustomerId, AssignedToUserId, CreatedByUserId, CreatedAt, UpdatedAt, DueDate, ResolvedAt, IsDeleted, IsSlaBreached, RequiresManagerReview, AffectedUsersCount)
        VALUES (@tn, 'SEED-OVERDUE-2026-05-28: overdue ' + CAST(@i AS VARCHAR), 'Overdue unresolved ticket for backlog/aging tests.',
                CASE WHEN @i < 4 THEN @elecCat ELSE @waterCat END,
                CASE WHEN @i < 3 THEN @critPri WHEN @i < 6 THEN @highPri ELSE @medPri END,
                @openStatus, @phoneSource,
                CASE WHEN @i % 2 = 0 THEN @damascusId ELSE @aleppoId END,
                @firstCustomer, NULL, @adminId,
                @overdueCreatedAt, @overdueCreatedAt, DATEADD(DAY, -5 - @i, @today), NULL, 0, 1, 0, 50 + @i);
        SET @i = @i + 1;
    END
    PRINT '── 8 overdue-unresolved tickets inserted.';
END
ELSE PRINT '── Overdue tickets already seeded.';

-- ─────────────────────────────────────────────────────────────────────────────
-- Done — sanity totals
-- ─────────────────────────────────────────────────────────────────────────────
PRINT '';
PRINT '── Post-seed totals:';
SELECT 'Tickets total' Metric, CAST(COUNT(*) AS VARCHAR) Val FROM Tickets WHERE IsDeleted=0
UNION ALL SELECT 'Tickets today',   CAST(COUNT(*) AS VARCHAR) FROM Tickets WHERE IsDeleted=0 AND CAST(CreatedAt AS DATE) = CAST(GETDATE() AS DATE)
UNION ALL SELECT 'Tickets this week', CAST(COUNT(*) AS VARCHAR) FROM Tickets WHERE IsDeleted=0 AND CreatedAt >= DATEADD(week, DATEDIFF(week, 0, GETDATE()), 0)
UNION ALL SELECT 'Tickets this month', CAST(COUNT(*) AS VARCHAR) FROM Tickets WHERE IsDeleted=0 AND CreatedAt >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)
UNION ALL SELECT 'Overdue unresolved', CAST(COUNT(*) AS VARCHAR) FROM Tickets WHERE IsDeleted=0 AND ResolvedAt IS NULL AND DueDate < GETDATE()
UNION ALL SELECT 'TicketComments',  CAST(COUNT(*) AS VARCHAR) FROM TicketComments
UNION ALL SELECT 'Active outages',  CAST(COUNT(*) AS VARCHAR) FROM Outages WHERE EndedAt IS NULL
UNION ALL SELECT 'Parent-child links', CAST(COUNT(*) AS VARCHAR) FROM Tickets WHERE ParentTicketId IS NOT NULL
UNION ALL SELECT 'Customers this month', CAST(COUNT(*) AS VARCHAR) FROM Customers WHERE SignupAt >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)
UNION ALL SELECT 'Customers no email', CAST(COUNT(*) AS VARCHAR) FROM Customers WHERE Email IS NULL OR Email='';
