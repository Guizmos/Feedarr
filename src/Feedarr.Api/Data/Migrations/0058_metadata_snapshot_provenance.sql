-- No historical data is guessed or rewritten. NULL means an unverified legacy snapshot.
ALTER TABLE media_entities ADD COLUMN ext_snapshot_version INTEGER;
ALTER TABLE releases ADD COLUMN ext_snapshot_version INTEGER;
