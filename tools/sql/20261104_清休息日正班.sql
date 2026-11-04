-- ═══════════════════════════════════════════════════════════════════════════
-- 数据订正：清掉 2026-09/10 月"休息日打卡"产生的正班工时（深圳GA 考勤组）
-- 执行时间：2026-11-04   执行人：运维（用户确认"清掉"）
--
-- 背景：9/5（周六）、9/6（周日）、10/10（周六）共 146 条记录，当时还没排班，
--       系统按"工作日"算出了正班工时（合计 997.3 小时）。
-- 口径：班次配置的每周休息日（深圳GA 白班/夜班均为周六日）打卡不产生正班工时，
--       周末上班必须走全天加班单；这些天的加班单都已审批，加班时长保留不动。
--
-- 结果：正班清零 146 条；月度汇总按"每天 FloorToHalf 后求和"重算，9 月 −964.5h、10 月 −11.5h；
--       出勤天数不受影响（休息日本来就不计入出勤）。
-- 备份：AttendanceDB.bak_restday_20261104（原样 146 条，含所有列，主键 Id）
-- 恢复：UPDATE AttendanceRecord r JOIN bak_restday_20261104 b ON b.Id=r.Id
--       SET r.ActualWorkHours=b.ActualWorkHours, r.OvertimeHours=b.OvertimeHours,
--           r.AttendanceStatus=b.AttendanceStatus, r.ApprovalNote=b.ApprovalNote;
--       （恢复后还需要重算 2026-09/2026-10 的月度汇总）
-- ═══════════════════════════════════════════════════════════════════════════

SET NAMES utf8mb4;

-- A. 备份原始记录（含所有列，可原样恢复）
DROP TABLE IF EXISTS AttendanceDB.bak_restday_20261104;
CREATE TABLE AttendanceDB.bak_restday_20261104 AS
SELECT r.* FROM AttendanceDB.AttendanceRecord r JOIN AttendanceDB.User u ON u.Id = r.UserId
WHERE u.AttendanceGroupId = 2
  AND r.WorkDate IN ('2026-09-05', '2026-09-06', '2026-10-10')
  AND r.ActualWorkHours > 0;
ALTER TABLE AttendanceDB.bak_restday_20261104 ADD PRIMARY KEY (Id);

-- B. 清零正班 + 记录原因；然后重算月度汇总
START TRANSACTION;

UPDATE AttendanceDB.AttendanceRecord r
JOIN AttendanceDB.bak_restday_20261104 b ON b.Id = r.Id
SET r.ApprovalNote = LEFT(CONCAT(IFNULL(r.ApprovalNote, ''),
        '；休息日打卡不产生正班（周末上班须走加班单），按口径清零：原 ', r.ActualWorkHours,
        ' 小时（2026-11-04 用户确认）'), 200),
    r.ActualWorkHours = 0,
    r.UpdatedAt = NOW();

-- TotalWorkHours = 每天 FloorToHalf(ActualWorkHours) 之和（与系统 AttendanceService.FloorToHalf 一致）
UPDATE AttendanceDB.MonthlyAttendanceSummary m
JOIN (
  SELECT t.UserId, mm.y AS Year, mm.m AS Month, ROUND(SUM(FLOOR(r2.ActualWorkHours * 2) / 2), 1) AS wh
  FROM (SELECT DISTINCT UserId FROM AttendanceDB.bak_restday_20261104) t
  CROSS JOIN (SELECT 2026 AS y, 9 AS m UNION ALL SELECT 2026, 10) mm
  JOIN AttendanceDB.AttendanceRecord r2
    ON r2.UserId = t.UserId
   AND r2.WorkDate >= STR_TO_DATE(CONCAT(mm.y, '-', mm.m, '-01'), '%Y-%m-%d')
   AND r2.WorkDate <  DATE_ADD(STR_TO_DATE(CONCAT(mm.y, '-', mm.m, '-01'), '%Y-%m-%d'), INTERVAL 1 MONTH)
  GROUP BY t.UserId, mm.y, mm.m
) s ON s.UserId = m.UserId AND s.Year = m.Year AND s.Month = m.Month
SET m.TotalWorkHours = s.wh, m.UpdatedAt = NOW()
WHERE m.Year = 2026 AND m.Month IN (9, 10);

COMMIT;

-- C. 校验
SELECT r.WorkDate, COUNT(*) AS 条数,
       ROUND(SUM(r.ActualWorkHours), 1) AS 正班合计,
       ROUND(SUM(r.OvertimeHours), 1)   AS 加班合计
FROM AttendanceDB.AttendanceRecord r
JOIN AttendanceDB.bak_restday_20261104 b ON b.Id = r.Id
GROUP BY r.WorkDate;
