"""
Creates the admin user in the SQLite dashboard database.
Usage:   python Scripts/seed_admin.py <password>
Requires: pip install bcrypt
Run from the GameServer/ directory after the server has started at least once
(so dashboard.db exists), or against the db file directly.
"""
import sys
import sqlite3
import bcrypt
from datetime import datetime, timezone

if len(sys.argv) < 2:
    print("Usage: python seed_admin.py <password>")
    sys.exit(1)

password = sys.argv[1].encode()
hashed   = bcrypt.hashpw(password, bcrypt.gensalt(rounds=12)).decode()
now      = datetime.now(timezone.utc).isoformat()

db_path = "dashboard.db"

con = sqlite3.connect(db_path)
con.execute("""
    CREATE TABLE IF NOT EXISTS Users (
        Id           INTEGER PRIMARY KEY AUTOINCREMENT,
        Username     TEXT NOT NULL UNIQUE,
        PasswordHash TEXT NOT NULL,
        Role         TEXT NOT NULL DEFAULT 'admin',
        CreatedAt    TEXT NOT NULL,
        LastLoginAt  TEXT
    )
""")
con.execute(
    "INSERT OR REPLACE INTO Users (Username, PasswordHash, Role, CreatedAt) "
    "VALUES (?, ?, 'admin', ?)",
    ("admin", hashed, now)
)
con.commit()
con.close()

print("Admin user created successfully.")
print("Username: admin")
print(f"Hash stored: {hashed[:20]}... (bcrypt, cost=12)")
