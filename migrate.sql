CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;
DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    ALTER DATABASE CHARACTER SET utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `AttendanceGroup` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `GroupName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `CompanyName` varchar(100) CHARACTER SET utf8mb4 NULL,
        `ClerkUserId` int NULL,
        `PunchRadiusMeters` int NOT NULL,
        `LocationLatitude` double NULL,
        `LocationLongitude` double NULL,
        `LocationName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `EnableLocationPunch` tinyint(1) NOT NULL,
        `LunchBreakMinutes` int NOT NULL,
        `DinnerBreakMinutes` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_AttendanceGroup` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `Department` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `DeptName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `DeptCode` varchar(50) CHARACTER SET utf8mb4 NULL,
        `ParentId` int NULL,
        `CompanyName` varchar(100) CHARACTER SET utf8mb4 NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_Department` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_Department_Department_ParentId` FOREIGN KEY (`ParentId`) REFERENCES `Department` (`Id`) ON DELETE SET NULL
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `ApprovalFlow` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AttendanceGroupId` int NOT NULL,
        `ApprovalType` int NOT NULL,
        `FlowName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `StepsConfig` longtext CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_ApprovalFlow` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_ApprovalFlow_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `Holiday` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `HolidayName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `HolidayDate` date NOT NULL,
        `HolidayType` int NOT NULL,
        `AttendanceGroupId` int NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_Holiday` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_Holiday_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `ShiftSchedule` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AttendanceGroupId` int NOT NULL,
        `ShiftName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `ShiftType` int NOT NULL,
        `WorkStartTime` time(6) NOT NULL,
        `WorkEndTime` time(6) NOT NULL,
        `LateToleranceMinutes` int NOT NULL,
        `EarlyLeaveToleranceMinutes` int NOT NULL,
        `EarliestClockInMinutes` int NOT NULL,
        `OvertimeThresholdMinutes` int NOT NULL,
        `IsCrossDay` tinyint(1) NOT NULL,
        `StandardWorkHours` decimal(65,30) NOT NULL,
        `Color` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_ShiftSchedule` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_ShiftSchedule_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `User` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `EmployeeNo` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `RealName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `PasswordHash` varchar(256) CHARACTER SET utf8mb4 NOT NULL,
        `DepartmentId` int NULL,
        `Position` varchar(100) CHARACTER SET utf8mb4 NULL,
        `Role` int NOT NULL,
        `AttendanceGroupId` int NULL,
        `SupervisorUserId` int NULL,
        `Phone` varchar(20) CHARACTER SET utf8mb4 NULL,
        `Email` varchar(100) CHARACTER SET utf8mb4 NULL,
        `AvatarUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
        `HireDate` date NULL,
        `IsActive` tinyint(1) NOT NULL,
        `LastLoginAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_User` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_User_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE SET NULL,
        CONSTRAINT `FK_User_Department_DepartmentId` FOREIGN KEY (`DepartmentId`) REFERENCES `Department` (`Id`) ON DELETE SET NULL,
        CONSTRAINT `FK_User_User_SupervisorUserId` FOREIGN KEY (`SupervisorUserId`) REFERENCES `User` (`Id`) ON DELETE SET NULL
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `ApprovalRequest` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RequestNo` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `ApplicantUserId` int NOT NULL,
        `ApprovalType` int NOT NULL,
        `ApprovalStatus` int NOT NULL,
        `PunchDate` date NULL,
        `PunchType` int NULL,
        `PunchTime` time(6) NULL,
        `LeaveType` int NULL,
        `LeaveStartTime` datetime(6) NULL,
        `LeaveEndTime` datetime(6) NULL,
        `LeaveDurationHours` decimal(65,30) NULL,
        `OvertimeStartTime` datetime(6) NULL,
        `OvertimeEndTime` datetime(6) NULL,
        `OvertimeDurationHours` decimal(65,30) NULL,
        `Reason` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `AttachmentUrls` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `SubmittedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_ApprovalRequest` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_ApprovalRequest_User_ApplicantUserId` FOREIGN KEY (`ApplicantUserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `AttendancePunch` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` int NOT NULL,
        `PunchTime` datetime(6) NOT NULL,
        `PunchType` int NOT NULL,
        `Latitude` double NULL,
        `Longitude` double NULL,
        `Address` varchar(500) CHARACTER SET utf8mb4 NULL,
        `DeviceInfo` varchar(200) CHARACTER SET utf8mb4 NULL,
        `IsValid` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_AttendancePunch` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_AttendancePunch_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `AttendanceRecord` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` int NOT NULL,
        `WorkDate` date NOT NULL,
        `ClockInTime` datetime(6) NULL,
        `ClockOutTime` datetime(6) NULL,
        `ScheduledStartTime` datetime(6) NULL,
        `ScheduledEndTime` datetime(6) NULL,
        `AttendanceStatus` int NOT NULL,
        `LateMinutes` int NOT NULL,
        `EarlyLeaveMinutes` int NOT NULL,
        `ActualWorkHours` decimal(65,30) NOT NULL,
        `OvertimeHours` decimal(65,30) NOT NULL,
        `IsHoliday` tinyint(1) NOT NULL,
        `ApprovalNote` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Remark` varchar(500) CHARACTER SET utf8mb4 NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_AttendanceRecord` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_AttendanceRecord_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `MonthlyAttendanceSummary` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` int NOT NULL,
        `Year` int NOT NULL,
        `Month` int NOT NULL,
        `ExpectedWorkdays` int NOT NULL,
        `ActualWorkdays` int NOT NULL,
        `LateCount` int NOT NULL,
        `EarlyLeaveCount` int NOT NULL,
        `AbsentDays` int NOT NULL,
        `NotPunchedCount` int NOT NULL,
        `LeaveDays` decimal(65,30) NOT NULL,
        `TotalOvertimeHours` decimal(65,30) NOT NULL,
        `TotalWorkHours` decimal(65,30) NOT NULL,
        `ApprovedCount` int NOT NULL,
        `GeneratedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_MonthlyAttendanceSummary` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_MonthlyAttendanceSummary_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `Notification` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` int NOT NULL,
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Content` varchar(2000) CHARACTER SET utf8mb4 NOT NULL,
        `NotificationType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `RelatedId` int NULL,
        `IsRead` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `ReadAt` datetime(6) NULL,
        CONSTRAINT `PK_Notification` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_Notification_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `ShiftAssignment` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` int NOT NULL,
        `ShiftScheduleId` int NOT NULL,
        `WorkDate` date NOT NULL,
        `IsAutoAssigned` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_ShiftAssignment` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_ShiftAssignment_ShiftSchedule_ShiftScheduleId` FOREIGN KEY (`ShiftScheduleId`) REFERENCES `ShiftSchedule` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_ShiftAssignment_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE TABLE `ApprovalStep` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ApprovalRequestId` int NOT NULL,
        `ApproverUserId` int NOT NULL,
        `StepOrder` int NOT NULL,
        `ApprovalStatus` int NOT NULL,
        `Comment` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `HandledAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_ApprovalStep` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_ApprovalStep_ApprovalRequest_ApprovalRequestId` FOREIGN KEY (`ApprovalRequestId`) REFERENCES `ApprovalRequest` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_ApprovalStep_User_ApproverUserId` FOREIGN KEY (`ApproverUserId`) REFERENCES `User` (`Id`) ON DELETE RESTRICT
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    INSERT INTO `AttendanceGroup` (`Id`, `ClerkUserId`, `CompanyName`, `CreatedAt`, `DinnerBreakMinutes`, `EnableLocationPunch`, `GroupName`, `IsActive`, `LocationLatitude`, `LocationLongitude`, `LocationName`, `LunchBreakMinutes`, `PunchRadiusMeters`, `UpdatedAt`)
    VALUES (1, NULL, '总公司', TIMESTAMP '2024-01-01 00:00:00', 30, FALSE, '总公司考勤组', TRUE, NULL, NULL, NULL, 60, 500, TIMESTAMP '2024-01-01 00:00:00');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    INSERT INTO `Department` (`Id`, `CompanyName`, `CreatedAt`, `DeptCode`, `DeptName`, `Description`, `IsActive`, `ParentId`, `UpdatedAt`)
    VALUES (1, '总公司', TIMESTAMP '2024-01-01 00:00:00', 'HQ', '总公司', NULL, TRUE, NULL, TIMESTAMP '2024-01-01 00:00:00');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    INSERT INTO `ShiftSchedule` (`Id`, `AttendanceGroupId`, `Color`, `CreatedAt`, `EarliestClockInMinutes`, `EarlyLeaveToleranceMinutes`, `IsActive`, `IsCrossDay`, `LateToleranceMinutes`, `OvertimeThresholdMinutes`, `ShiftName`, `ShiftType`, `StandardWorkHours`, `UpdatedAt`, `WorkEndTime`, `WorkStartTime`)
    VALUES (1, 1, '#1890ff', TIMESTAMP '2024-01-01 00:00:00', 60, 5, TRUE, FALSE, 5, 30, '正常班', 1, 8.0, TIMESTAMP '2024-01-01 00:00:00', TIME '18:00:00', TIME '09:00:00');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ApprovalFlow_AttendanceGroupId` ON `ApprovalFlow` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ApprovalRequest_ApplicantUserId_ApprovalStatus` ON `ApprovalRequest` (`ApplicantUserId`, `ApprovalStatus`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_ApprovalRequest_RequestNo` ON `ApprovalRequest` (`RequestNo`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ApprovalStep_ApprovalRequestId` ON `ApprovalStep` (`ApprovalRequestId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ApprovalStep_ApproverUserId` ON `ApprovalStep` (`ApproverUserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_AttendancePunch_UserId_PunchTime` ON `AttendancePunch` (`UserId`, `PunchTime`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_AttendanceRecord_UserId_WorkDate` ON `AttendanceRecord` (`UserId`, `WorkDate`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_Department_ParentId` ON `Department` (`ParentId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_Holiday_AttendanceGroupId` ON `Holiday` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_Holiday_HolidayDate_AttendanceGroupId` ON `Holiday` (`HolidayDate`, `AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_MonthlyAttendanceSummary_UserId_Year_Month` ON `MonthlyAttendanceSummary` (`UserId`, `Year`, `Month`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_Notification_UserId` ON `Notification` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ShiftAssignment_ShiftScheduleId` ON `ShiftAssignment` (`ShiftScheduleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_ShiftAssignment_UserId_WorkDate` ON `ShiftAssignment` (`UserId`, `WorkDate`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_ShiftSchedule_AttendanceGroupId` ON `ShiftSchedule` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_User_AttendanceGroupId` ON `User` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_User_DepartmentId` ON `User` (`DepartmentId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_User_EmployeeNo` ON `User` (`EmployeeNo`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_User_Phone` ON `User` (`Phone`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    CREATE INDEX `IX_User_SupervisorUserId` ON `User` (`SupervisorUserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260618023257_InitialCreate') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260618023257_InitialCreate', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260624010016_AddDingTalkUserId') THEN

    ALTER TABLE `User` ADD `DingTalkUserId` varchar(64) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260624010016_AddDingTalkUserId') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260624010016_AddDingTalkUserId', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260701120000_AddBlacklistAndDeptSort') THEN

    ALTER TABLE `User` ADD `IsBlacklisted` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260701120000_AddBlacklistAndDeptSort') THEN

    ALTER TABLE `Department` ADD `SortIndex` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260701120000_AddBlacklistAndDeptSort') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260701120000_AddBlacklistAndDeptSort', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260702200000_AddAttendanceGroupDepartmentId') THEN

    ALTER TABLE `AttendanceGroup` ADD `DepartmentId` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260702200000_AddAttendanceGroupDepartmentId') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260702200000_AddAttendanceGroupDepartmentId', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260703120000_AddBusinessTrip') THEN

    ALTER TABLE `ApprovalRequest` ADD `BusinessTripStartTime` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260703120000_AddBusinessTrip') THEN

    ALTER TABLE `ApprovalRequest` ADD `BusinessTripEndTime` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260703120000_AddBusinessTrip') THEN

    ALTER TABLE `ApprovalRequest` ADD `BusinessTripDestination` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260703120000_AddBusinessTrip') THEN

    ALTER TABLE `ApprovalRequest` ADD `BusinessTripDurationDays` decimal(65,30) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260703120000_AddBusinessTrip') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260703120000_AddBusinessTrip', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707011708_AddGroupApproversRemoveApprovalFlow') THEN

    DROP TABLE `ApprovalFlow`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707011708_AddGroupApproversRemoveApprovalFlow') THEN

    CREATE TABLE `AttendanceGroupApprover` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AttendanceGroupId` int NOT NULL,
        `UserId` int NOT NULL,
        CONSTRAINT `PK_AttendanceGroupApprover` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_AttendanceGroupApprover_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_AttendanceGroupApprover_User_UserId` FOREIGN KEY (`UserId`) REFERENCES `User` (`Id`) ON DELETE RESTRICT
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707011708_AddGroupApproversRemoveApprovalFlow') THEN

    CREATE UNIQUE INDEX `IX_AttendanceGroupApprover_AttendanceGroupId_UserId` ON `AttendanceGroupApprover` (`AttendanceGroupId`, `UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707011708_AddGroupApproversRemoveApprovalFlow') THEN

    CREATE INDEX `IX_AttendanceGroupApprover_UserId` ON `AttendanceGroupApprover` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707011708_AddGroupApproversRemoveApprovalFlow') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260707011708_AddGroupApproversRemoveApprovalFlow', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    ALTER TABLE `User` ADD `IdNumber` varchar(18) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    CREATE TABLE `EmployeeRegistration` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RealName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `Phone` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `IdNumber` varchar(18) CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL,
        `SubmittedAt` datetime(6) NOT NULL,
        `ReviewedAt` datetime(6) NULL,
        `ConfirmedUserId` int NULL,
        `RejectReason` varchar(200) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_EmployeeRegistration` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_EmployeeRegistration_User_ConfirmedUserId` FOREIGN KEY (`ConfirmedUserId`) REFERENCES `User` (`Id`) ON DELETE SET NULL
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    CREATE INDEX `IX_User_IdNumber` ON `User` (`IdNumber`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    CREATE INDEX `IX_EmployeeRegistration_ConfirmedUserId` ON `EmployeeRegistration` (`ConfirmedUserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    CREATE INDEX `IX_EmployeeRegistration_IdNumber` ON `EmployeeRegistration` (`IdNumber`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    CREATE INDEX `IX_EmployeeRegistration_Phone` ON `EmployeeRegistration` (`Phone`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707013627_AddEmployeeRegistrationAndUserIdNumber') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260707013627_AddEmployeeRegistrationAndUserIdNumber', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707015933_AddUserContractCompany') THEN

    ALTER TABLE `User` ADD `ContractCompany` varchar(100) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707015933_AddUserContractCompany') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260707015933_AddUserContractCompany', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `Department` ADD `AttendanceGroupId` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceRecord` ADD `LocationAbnormal` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceRecord` ADD `LocationAbnormalNote` varchar(300) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendancePunch` ADD `LocationValid` tinyint(1) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    CREATE TABLE `AttendanceGroupLocation` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AttendanceGroupId` int NOT NULL,
        `LocationName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Latitude` double NOT NULL,
        `Longitude` double NOT NULL,
        `RadiusMeters` int NOT NULL,
        CONSTRAINT `PK_AttendanceGroupLocation` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_AttendanceGroupLocation_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    UPDATE `Department` SET `AttendanceGroupId` = NULL
    WHERE `Id` = 1;
    SELECT ROW_COUNT();


    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    CREATE INDEX `IX_Department_AttendanceGroupId` ON `Department` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    CREATE INDEX `IX_AttendanceGroupLocation_AttendanceGroupId` ON `AttendanceGroupLocation` (`AttendanceGroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `Department` ADD CONSTRAINT `FK_Department_AttendanceGroup_AttendanceGroupId` FOREIGN KEY (`AttendanceGroupId`) REFERENCES `AttendanceGroup` (`Id`) ON DELETE SET NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN


                    INSERT INTO `AttendanceGroupLocation` (`AttendanceGroupId`, `LocationName`, `Latitude`, `Longitude`, `RadiusMeters`)
                    SELECT `Id`, `LocationName`, `LocationLatitude`, `LocationLongitude`, `PunchRadiusMeters`
                    FROM `AttendanceGroup`
                    WHERE `LocationLatitude` IS NOT NULL AND `LocationLongitude` IS NOT NULL;
                

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN


                    UPDATE `Department` d
                    JOIN `AttendanceGroup` g ON g.`DepartmentId` = d.`Id`
                    SET d.`AttendanceGroupId` = g.`Id`;
                

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceGroup` DROP COLUMN `DepartmentId`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceGroup` DROP COLUMN `LocationLatitude`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceGroup` DROP COLUMN `LocationLongitude`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceGroup` DROP COLUMN `LocationName`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    ALTER TABLE `AttendanceGroup` DROP COLUMN `PunchRadiusMeters`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707022446_AddMultiLocationAndDeptGroupFollow') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260707022446_AddMultiLocationAndDeptGroupFollow', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707031659_AddShiftScheduleRestDaysOfWeek') THEN

    ALTER TABLE `ShiftSchedule` ADD `RestDaysOfWeek` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707031659_AddShiftScheduleRestDaysOfWeek') THEN

    UPDATE `ShiftSchedule` SET `RestDaysOfWeek` = '0,6' WHERE `RestDaysOfWeek` = '';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707031659_AddShiftScheduleRestDaysOfWeek') THEN

    UPDATE `ShiftSchedule` SET `RestDaysOfWeek` = '0,6'
    WHERE `Id` = 1;
    SELECT ROW_COUNT();


    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260707031659_AddShiftScheduleRestDaysOfWeek') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260707031659_AddShiftScheduleRestDaysOfWeek', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708062632_AddUserEmergencyContactAndAddress') THEN

    ALTER TABLE `User` ADD `EmergencyContactName` varchar(50) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708062632_AddUserEmergencyContactAndAddress') THEN

    ALTER TABLE `User` ADD `EmergencyContactPhone` varchar(20) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708062632_AddUserEmergencyContactAndAddress') THEN

    ALTER TABLE `User` ADD `HomeAddress` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708062632_AddUserEmergencyContactAndAddress') THEN

    ALTER TABLE `User` ADD `IdCardPhotoUrl` varchar(500) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708062632_AddUserEmergencyContactAndAddress') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260708062632_AddUserEmergencyContactAndAddress', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `ContractCompany` varchar(100) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `EmergencyContactName` varchar(50) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `EmergencyContactPhone` varchar(20) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `HomeAddress` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `IdCardPhotoUrl` varchar(500) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    ALTER TABLE `EmployeeRegistration` ADD `Position` varchar(100) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260708065448_AddEmployeeRegistrationExtraFields') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260708065448_AddEmployeeRegistrationExtraFields', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260714004037_AddMidCheckWindow') THEN

    ALTER TABLE `ShiftSchedule` ADD `MidCheckEndTime` time(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260714004037_AddMidCheckWindow') THEN

    ALTER TABLE `ShiftSchedule` ADD `MidCheckStartTime` time(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260714004037_AddMidCheckWindow') THEN

    ALTER TABLE `AttendanceRecord` ADD `MidCheckTime` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260714004037_AddMidCheckWindow') THEN

    UPDATE `ShiftSchedule` SET `MidCheckEndTime` = NULL, `MidCheckStartTime` = NULL
    WHERE `Id` = 1;
    SELECT ROW_COUNT();


    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260714004037_AddMidCheckWindow') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260714004037_AddMidCheckWindow', '9.0.9');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

