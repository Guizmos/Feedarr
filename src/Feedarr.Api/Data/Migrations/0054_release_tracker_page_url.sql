-- Explicit, qualified tracker detail page. Historical rows intentionally remain NULL.
ALTER TABLE releases ADD COLUMN tracker_page_url TEXT;
