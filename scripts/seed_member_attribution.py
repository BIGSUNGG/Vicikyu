"""귀속 규칙 실화면 검증용 개발 DB 시드 — 운영과 무관한 로컬 개발 DB 전용.

기존 출석 시트 정의(SpreadsheetId 일치)를 재사용해 학기 탭 매핑(봄부원 2026-1, 가을부원 2026-2)과
마감 앱 활동 2건(2026-03-10, 2026-10-10)을 넣는다. 시트 원본은 읽지도 쓰지도 않는다.
멱등: 이전 실행분 매핑(RowLabel 종속)·활동(CreatedByUserId)만 삭제한다.
출력: JSON {"sheetId":n,"march":id,"october":id}
"""

import json
import sqlite3
import sys
from datetime import datetime, timezone


def iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%d %H:%M:%S")


SEED_ROW_LABELS = ("봄부원 26 검증", "가을부원 26 검증")
SEED_CREATOR = "verify-attribution"


def main() -> None:
    db_path, spring_id, autumn_id, spreadsheet_id = (
        sys.argv[1],
        sys.argv[2],
        sys.argv[3],
        sys.argv[4],
    )
    now = iso(datetime.now(timezone.utc).replace(tzinfo=None))
    con = sqlite3.connect(db_path)
    try:
        cur = con.cursor()

        # (1) 이전 실행분 정리 — 시드 전용 식별자(행 라벨·CreatedByUserId)로만 삭제한다
        for label in SEED_ROW_LABELS:
            cur.execute("DELETE FROM SheetMemberMappings WHERE RowLabel = ?", (label,))
        for (activity_id,) in cur.execute(
            "SELECT Id FROM Activities WHERE CreatedByUserId = ?", (SEED_CREATOR,)
        ).fetchall():
            cur.execute("DELETE FROM Attendances WHERE ActivityId = ?", (activity_id,))
            cur.execute("DELETE FROM Activities WHERE Id = ?", (activity_id,))

        # (2) 출석 시트 정의 — 같은 SpreadsheetId가 있으면 재사용, 없으면 만든다(Kind 0 = Attendance)
        row = cur.execute(
            "SELECT Id FROM SheetDefinitions WHERE SpreadsheetId = ? AND Kind = 0",
            (spreadsheet_id,),
        ).fetchone()
        if row is not None:
            sheet_id = row[0]
        else:
            cur.execute(
                "INSERT INTO SheetDefinitions (Name, SpreadsheetId, Kind, CreatedAt, UpdatedAt) VALUES (?,?,?,?,?)",
                ("검증 출석부", spreadsheet_id, 0, now, now),
            )
            sheet_id = cur.lastrowid
            if sheet_id is None:
                raise SystemExit("SheetDefinitions insert produced no row id")

        # (3) 학기 탭 매핑(봄부원 2026-1, 가을부원 2026-2)
        for tab_name, label, user_id in (
            ("2026-1", SEED_ROW_LABELS[0], spring_id),
            ("2026-2", SEED_ROW_LABELS[1], autumn_id),
        ):
            cur.execute(
                "INSERT INTO SheetMemberMappings (SheetDefinitionId, TabName, RowLabel, UserId) VALUES (?,?,?,?)",
                (sheet_id, tab_name, label, user_id),
            )

        # (4) 마감 앱 활동 2건 — Status 4 = ActivityClosed(같은 날짜 마감 유니크 인덱스 회피 위해 날짜 상이)
        def seed_activity(date_text: str) -> int:
            cur.execute(
                "INSERT INTO Activities (ActivityDate, Status, CreatedByUserId, CreatedAt, UpdatedAt,"
                " AttendanceOpenedAt, AttendanceClosedAt, ActivityClosedAt, SheetSyncRequiredUserIds, SheetSyncRevision)"
                " VALUES (?,?,?,?,?,?,?,?,?,?)",
                (date_text, 4, SEED_CREATOR, now, now, now, now, now, "[]", 0),
            )
            activity_id = cur.lastrowid
            if activity_id is None:
                raise SystemExit("Activities insert produced no row id")
            return activity_id

        march = seed_activity("2026-03-10")
        october = seed_activity("2026-10-10")
        con.commit()

        # (5) 결과 검증 — 삽입된 행을 다시 읽어 확정한다(정적 SQL, 행 단위 카운트)
        seeded_activities = cur.execute(
            "SELECT COUNT(*) FROM Activities WHERE CreatedByUserId = ?", (SEED_CREATOR,)
        ).fetchone()[0]
        seeded_mappings = 0
        for label in SEED_ROW_LABELS:
            seeded_mappings += cur.execute(
                "SELECT COUNT(*) FROM SheetMemberMappings WHERE RowLabel = ?", (label,)
            ).fetchone()[0]
        if (seeded_activities, seeded_mappings) != (2, 2):
            raise SystemExit(f"seed mismatch: {seeded_activities}, {seeded_mappings}")
        print(json.dumps({"sheetId": sheet_id, "march": march, "october": october}))
    finally:
        con.close()


if __name__ == "__main__":
    main()
