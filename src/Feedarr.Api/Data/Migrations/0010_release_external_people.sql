PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN ext_directors TEXT;
ALTER TABLE releases ADD COLUMN ext_writers TEXT;
ALTER TABLE releases ADD COLUMN ext_cast TEXT;
