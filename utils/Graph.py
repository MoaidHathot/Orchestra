"""
Microsoft Graph API client using dual-token approach:
- Azure CLI token: for listing teams/channels structure (has Group.ReadWrite.All)
- WorkIQ token: for reading messages/mail (has Chat.Read, Mail.Read, ChannelMessage.Read.All)
"""

from __future__ import annotations

import json
import subprocess
import webbrowser
import requests
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlencode, urlparse, parse_qs
from pathlib import Path
from typing import Optional
from datetime import datetime, timedelta
import re

# WorkIQ's pre-consented app ID (has admin consent at Microsoft tenant)
CLIENT_ID = "ba081686-5d24-4bc6-a0d6-d034ecffed87"
TENANT_ID = "72f988bf-86f1-41af-91ab-2d7cd011db47"
REDIRECT_URI = "http://localhost:8400"
SCOPES = [
    "https://graph.microsoft.com/Chat.Read",
    "https://graph.microsoft.com/Mail.Read",
    "https://graph.microsoft.com/ChannelMessage.Read.All",
    "https://graph.microsoft.com/Sites.Read.All",
    "https://graph.microsoft.com/People.Read.All",
    "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
    "https://graph.microsoft.com/ExternalItem.Read.All",
    "offline_access",
    "openid",
    "profile",
    "email",
]
TOKEN_CACHE = Path.home() / ".graph_token.json"
GRAPH_BASE = "https://graph.microsoft.com/v1.0"


def get_az_cli_token() -> Optional[str]:
    """Get Graph token from Azure CLI (has broader listing permissions)."""
    try:
        result = subprocess.run(
            ["az", "account", "get-access-token", "--resource", "https://graph.microsoft.com", "--query", "accessToken", "-o", "tsv"],
            capture_output=True, text=True, timeout=30, shell=True
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except Exception:
        pass
    return None


class AuthHandler(BaseHTTPRequestHandler):
    """HTTP handler to capture OAuth redirect."""
    auth_code: Optional[str] = None
    error: Optional[str] = None

    def do_GET(self):
        query = parse_qs(urlparse(self.path).query)
        AuthHandler.auth_code = query.get("code", [None])[0]
        AuthHandler.error = query.get("error_description", [None])[0]
        
        self.send_response(200)
        self.send_header("Content-type", "text/html")
        self.end_headers()
        self.wfile.write(b"<html><body><h2>Authentication complete!</h2><p>You can close this window.</p></body></html>")

    def log_message(self, *args):
        pass  # Suppress HTTP logs


class GraphClient:
    """
    Microsoft Graph API client using dual-token approach:
    - Azure CLI token: for listing teams/channels (has Group.ReadWrite.All)
    - WorkIQ token: for reading messages (has Chat.Read, Mail.Read, ChannelMessage.Read.All)
    """

    def __init__(self):
        self.access_token: Optional[str] = None  # WorkIQ token for messages
        self.refresh_token: Optional[str] = None
        self.az_token: Optional[str] = None      # Azure CLI token for listing
        self._load_cached_token()
        self._load_az_token()

    def _load_az_token(self):
        """Load token from Azure CLI."""
        self.az_token = get_az_cli_token()

    def _load_cached_token(self):
        """Load WorkIQ token from cache if valid."""
        if TOKEN_CACHE.exists():
            try:
                data = json.loads(TOKEN_CACHE.read_text())
                self.access_token = data.get("access_token")
                self.refresh_token = data.get("refresh_token")
            except Exception:
                pass

    def _save_token(self, token_response: dict):
        """Cache tokens to disk."""
        self.access_token = token_response["access_token"]
        self.refresh_token = token_response.get("refresh_token", self.refresh_token)
        TOKEN_CACHE.write_text(json.dumps({
            "access_token": self.access_token,
            "refresh_token": self.refresh_token,
        }))

    def _refresh_access_token(self) -> bool:
        """Refresh access token using refresh token."""
        if not self.refresh_token:
            return False
        try:
            resp = requests.post(
                f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token",
                data={
                    "client_id": CLIENT_ID,
                    "grant_type": "refresh_token",
                    "refresh_token": self.refresh_token,
                    "scope": " ".join(SCOPES),
                },
            )
            if resp.ok:
                self._save_token(resp.json())
                return True
        except Exception:
            pass
        return False

    def authenticate(self, force: bool = False) -> bool:
        """
        Authenticate via interactive browser flow.
        Returns True if successful.
        """
        # Always try refresh first if we have a token (it may be stale)
        if self.access_token and not force:
            # Validate token works with a quick API call
            try:
                requests.get(
                    f"{GRAPH_BASE}/me",
                    headers={"Authorization": f"Bearer {self.access_token}"},
                    timeout=5
                ).raise_for_status()
                return True  # Token is valid
            except Exception:
                pass  # Token invalid, try refresh
        
        # Try refresh
        if self._refresh_access_token():
            return True

        # Interactive browser auth
        auth_url = f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/authorize?" + urlencode({
            "client_id": CLIENT_ID,
            "response_type": "code",
            "redirect_uri": REDIRECT_URI,
            "scope": " ".join(SCOPES),
            "response_mode": "query",
        })

        # Start local server
        server = HTTPServer(("localhost", 8400), AuthHandler)
        server.timeout = 120

        print("Opening browser for authentication...")
        webbrowser.open(auth_url)

        # Wait for callback
        AuthHandler.auth_code = None
        AuthHandler.error = None
        server.handle_request()
        server.server_close()

        if AuthHandler.error:
            print(f"Auth error: {AuthHandler.error}")
            return False

        if not AuthHandler.auth_code:
            print("No auth code received")
            return False

        # Exchange code for token
        resp = requests.post(
            f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token",
            data={
                "client_id": CLIENT_ID,
                "grant_type": "authorization_code",
                "code": AuthHandler.auth_code,
                "redirect_uri": REDIRECT_URI,
                "scope": " ".join(SCOPES),
            },
        )

        if not resp.ok:
            print(f"Token exchange failed: {resp.text}")
            return False

        self._save_token(resp.json())
        print("Authentication successful!")
        return True

    def _get(self, endpoint: str, params: Optional[dict] = None, use_az: bool = False) -> dict:
        """Make authenticated GET request."""
        token = self.az_token if use_az else self.access_token
        if not token:
            raise RuntimeError(f"No {'Azure CLI' if use_az else 'WorkIQ'} token available")
        
        resp = requests.get(
            f"{GRAPH_BASE}{endpoint}",
            headers={"Authorization": f"Bearer {token}"},
            params=params,
        )
        resp.raise_for_status()
        return resp.json()

    @staticmethod
    def _strip_html(html: str) -> str:
        """Remove HTML tags from string."""
        text = re.sub(r'<[^>]+>', '', html)
        text = text.replace('&nbsp;', ' ').replace('&#160;', ' ')
        return re.sub(r'\s+', ' ', text).strip()

    @staticmethod
    def _format_time(iso_time: str) -> str:
        """Format ISO time to readable format."""
        if not iso_time:
            return ""
        try:
            # Handle various ISO formats
            iso_time = iso_time.replace('Z', '+00:00')
            # Python's fromisoformat can't handle fractional seconds with less than 6 digits
            if '.' in iso_time:
                base, rest = iso_time.split('.', 1)
                # Find where the timezone starts (+ or - after the decimal)
                for i, c in enumerate(rest):
                    if c in '+-':
                        frac = rest[:i].ljust(6, '0')[:6]
                        tz = rest[i:]
                        iso_time = f"{base}.{frac}{tz}"
                        break
            dt = datetime.fromisoformat(iso_time)
            return dt.strftime("%m/%d %H:%M")
        except Exception:
            return iso_time[:16] if len(iso_time) > 16 else iso_time

    # ─────────────────────────────────────────────────────────────
    # API Methods
    # ─────────────────────────────────────────────────────────────

    def ask_copilot(self, question: str, timezone: str = "Australia/Sydney") -> dict:
        """
        Ask Microsoft 365 Copilot a question via the Chat API.
        Returns the full response including messages and attributions.
        
        Args:
            question: The question to ask Copilot
            timezone: User's timezone for location context
            
        Returns:
            Full Copilot conversation response dict
        """
        if not self.access_token:
            raise RuntimeError("Not authenticated. Call authenticate() first.")
        
        headers = {
            "Authorization": f"Bearer {self.access_token}",
            "Content-Type": "application/json",
        }
        
        # Step 1: Create conversation
        resp = requests.post(
            f"{GRAPH_BASE.replace('/v1.0', '/beta')}/copilot/conversations",
            headers=headers,
            json={},
        )
        resp.raise_for_status()
        conversation_id = resp.json()["id"]
        
        # Step 2: Send message
        resp = requests.post(
            f"{GRAPH_BASE.replace('/v1.0', '/beta')}/copilot/conversations/{conversation_id}/chat",
            headers=headers,
            json={
                "message": {"text": question},
                "locationHint": {"timeZone": timezone},
            },
        )
        resp.raise_for_status()
        return resp.json()

    def ask_copilot_simple(self, question: str, timezone: str = "Australia/Sydney") -> str:
        """
        Ask Microsoft 365 Copilot a question and return just the text response.
        
        Args:
            question: The question to ask Copilot
            timezone: User's timezone for location context
            
        Returns:
            Copilot's text response
        """
        response = self.ask_copilot(question, timezone)
        messages = response.get("messages", [])
        # Find the response message (second message, from Copilot)
        for msg in messages:
            if msg.get("id") != messages[0].get("id"):  # Skip the echo of user's question
                return msg.get("text", "")
        return ""

    def get_me(self) -> dict:
        """Get current user info (uses Azure CLI token)."""
        return self._get("/me", use_az=True)

    def get_chats(self, top: int = 20) -> list:
        """Get user's chats (1:1, group, meeting)."""
        return self._get("/me/chats", {"$top": top, "$expand": "members"}).get("value", [])

    def find_chat_by_member(self, email: str, chat_type: Optional[str] = None) -> list:
        """
        Find chats that include a specific member by email.
        For 1:1 chats, constructs the chat ID directly from user IDs (instant).
        For other types, pages through chats with member expansion.
        """
        if chat_type == "oneOnOne":
            chat = self.get_oneononone_chat_by_email(email)
            return [chat] if chat else []

        # For group/meeting: page through chats (no server-side member filter available)
        results = []
        url = f"{GRAPH_BASE}/me/chats"
        params = {"$top": 50, "$expand": "members,lastMessagePreview"}
        if chat_type:
            params["$filter"] = f"chatType eq '{chat_type}'"
        headers = {"Authorization": f"Bearer {self.access_token}"}
        while url:
            resp = requests.get(url, headers=headers, params=params)
            resp.raise_for_status()
            data = resp.json()
            for c in data.get("value", []):
                for m in c.get("members", []):
                    if (m.get("email") or "").lower() == email.lower():
                        results.append(c)
                        break
            url = data.get("@odata.nextLink")
            params = None  # nextLink includes params
        return results

    def get_oneononone_chat_by_email(self, email: str) -> Optional[dict]:
        """
        Get a 1:1 chat with a specific user by their email.
        Resolves the user's ID, constructs the chat ID, and fetches it directly.
        Returns the chat dict with lastMessagePreview and members, or None if not found.
        """
        # Resolve user ID
        try:
            user = self._get(f"/users/{email}", {"$select": "id,displayName,mail"}, use_az=True)
        except Exception:
            return None

        their_id = user.get("id")
        if not their_id:
            return None

        # Get my ID
        me = self._get("/me", {"$select": "id"}, use_az=True)
        my_id = me.get("id")
        if not my_id:
            return None

        # Construct 1:1 chat ID (sorted user IDs)
        ids = sorted([my_id, their_id])
        chat_id = f"19:{ids[0]}_{ids[1]}@unq.gbl.spaces"

        try:
            return self._get(f"/me/chats/{chat_id}", {"$expand": "lastMessagePreview,members"})
        except Exception:
            return None

    def find_chat_by_topic(self, topic: str) -> list:
        """
        Find chats whose topic contains the given text.
        Uses server-side $filter with contains() for fast lookup.
        """
        results = []
        url = f"{GRAPH_BASE}/me/chats"
        escaped = topic.replace("'", "''")
        params = {
            "$top": 50,
            "$filter": f"contains(topic, '{escaped}')",
            "$expand": "lastMessagePreview",
        }
        headers = {"Authorization": f"Bearer {self.access_token}"}
        while url:
            resp = requests.get(url, headers=headers, params=params)
            resp.raise_for_status()
            data = resp.json()
            page = data.get("value", [])
            if not page:
                break
            results.extend(page)
            url = data.get("@odata.nextLink")
            params = None
        return results

    def get_chat_with_last_activity(self, chat_id: str) -> dict:
        """Get a single chat by ID with lastMessagePreview and members expanded."""
        return self._get(f"/me/chats/{chat_id}", {"$expand": "lastMessagePreview,members"})

    def get_chat_messages(self, chat_id: str, top: int = 10) -> list:
        """Get messages from a specific chat."""
        return self._get(f"/me/chats/{chat_id}/messages", {"$top": top}).get("value", [])

    def get_joined_teams(self) -> list:
        """Get teams the user is a member of (uses Azure CLI token)."""
        return self._get("/me/joinedTeams", use_az=True).get("value", [])

    def get_team_channels(self, team_id: str) -> list:
        """Get channels in a team (uses Azure CLI token)."""
        return self._get(f"/teams/{team_id}/channels", use_az=True).get("value", [])

    def get_channel_messages(self, team_id: str, channel_id: str, top: int = 10) -> list:
        """Get messages from a team channel."""
        return self._get(f"/teams/{team_id}/channels/{channel_id}/messages", {"$top": top}).get("value", [])

    def get_thread_replies(self, team_id: str, channel_id: str, message_id: str, top: int = 1) -> list:
        """Get replies to a specific channel message (thread), newest first."""
        return self._get(
            f"/teams/{team_id}/channels/{channel_id}/messages/{message_id}/replies",
            {"$top": top},
        ).get("value", [])

    def get_mail(self, top: int = 10) -> list:
        """Get user's mail messages. Requires WorkIQ token (browser auth)."""
        return self._get("/me/messages", {"$top": top, "$select": "subject,from,receivedDateTime,bodyPreview"}).get("value", [])

    def get_mail_folder_messages(self, folder: str = "Inbox", days: int = 7, top: int = 50, include_body: bool = False) -> list:
        """
        Get messages from a specific mail folder (default: Inbox) from the last N days (default: 7).

        Args:
            folder: Folder display name (e.g., 'Inbox', 'Sent Items') or well-known name.
            days: Lookback window in days.
            top: Max number of messages to return.
            include_body: When True, also include message body content (text) in results.
        """
        if not self.access_token:
            raise RuntimeError("Not authenticated. Call authenticate() first.")
        if days < 0:
            raise ValueError("days must be >= 0")
        if top <= 0:
            return []

        norm = re.sub(r"\s+", "", (folder or "Inbox")).lower()
        well_known = {
            "inbox": "inbox",
            "sent": "sentitems",
            "sentitems": "sentitems",
            "drafts": "drafts",
            "archive": "archive",
            "junkemail": "junkemail",
        }

        time_field = "sentDateTime" if norm in ("sent", "sentitems") else "receivedDateTime"
        start = datetime.utcnow() - timedelta(days=days)
        start_iso = start.replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")

        if norm in well_known:
            endpoint = f"/me/mailFolders/{well_known[norm]}/messages"
        else:
            escaped = (folder or "").replace("'", "''")
            data = self._get(
                "/me/mailFolders",
                {"$top": 100, "$filter": f"displayName eq '{escaped}'", "$select": "id,displayName"},
            )
            folders = data.get("value", [])
            if not folders:
                raise ValueError(f"Mail folder not found: {folder!r}")
            endpoint = f"/me/mailFolders/{folders[0]['id']}/messages"

        results: list[dict] = []
        url = f"{GRAPH_BASE}{endpoint}"
        select_fields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,sentDateTime,conversationId,webLink,bodyPreview"
        if include_body:
            select_fields += ",body"

        params = {
            "$top": min(50, top),
            "$select": select_fields,
            "$orderby": f"{time_field} desc",
            "$filter": f"{time_field} ge {start_iso}",
        }
        headers = {"Authorization": f"Bearer {self.access_token}"}
        if include_body:
            # Request plain-text body to avoid HTML parsing downstream.
            headers["Prefer"] = 'outlook.body-content-type="text"'
        remaining = top

        while url and remaining > 0:
            # nextLink already contains params
            if params is not None:
                params["$top"] = min(50, remaining)

            resp = requests.get(url, headers=headers, params=params)
            resp.raise_for_status()
            data = resp.json()
            page = data.get("value", [])
            results.extend(page)
            remaining -= len(page)
            url = data.get("@odata.nextLink")
            params = None

        return results

    def search_mail(self, query: str, top: int = 5) -> list:
        """
        Search mail using the Microsoft Search API.
        Supports KQL queries like subject:"exact phrase" or free text.
        Returns list of matching messages sorted by relevance.
        """
        if not self.access_token:
            raise RuntimeError("Not authenticated. Call authenticate() first.")
        resp = requests.post(
            f"{GRAPH_BASE}/search/query",
            headers={
                "Authorization": f"Bearer {self.access_token}",
                "Content-Type": "application/json",
            },
            json={
                "requests": [{
                    "entityTypes": ["message"],
                    "query": {"queryString": query},
                    "from": 0,
                    "size": top,
                }]
            },
        )
        resp.raise_for_status()
        results = []
        for container in resp.json().get("value", []):
            for hc in container.get("hitsContainers", []):
                for hit in hc.get("hits", []):
                    results.append(hit.get("resource", {}))
        return results

    # ─────────────────────────────────────────────────────────────
    # Convenience Methods (formatted output)
    # ─────────────────────────────────────────────────────────────

    def print_chats_by_type(self, chat_type: str, limit: int = 3, messages_per_chat: int = 5):
        """Print chats of a specific type with recent messages."""
        chats = [c for c in self.get_chats() if c.get("chatType") == chat_type][:limit]
        
        for chat in chats:
            # Determine chat title
            if chat_type == "oneOnOne":
                members = chat.get("members", [])
                other = next((m.get("displayName") for m in members if "maxtarasuik" not in m.get("email", "").lower()), "Unknown")
                title = f"Chat with: {other}"
            else:
                title = chat.get("topic") or f"{chat_type} chat"
            
            print(f"\n--- {title} ---")
            
            messages = self.get_chat_messages(chat["id"], messages_per_chat)
            for msg in messages:
                if msg.get("messageType") != "message" or not msg.get("from", {}).get("user"):
                    continue
                sender = msg["from"]["user"].get("displayName", "Unknown")
                body = self._strip_html(msg.get("body", {}).get("content", ""))[:120]
                time = self._format_time(msg.get("createdDateTime", ""))
                if body:
                    print(f"  [{time}] {sender}: {body}")

    def print_channel_messages(self, team_id: str, channel_ids: list[tuple[str, str]], messages_per_channel: int = 5):
        """Print messages from specified channels. channel_ids is list of (name, id) tuples."""
        for name, channel_id in channel_ids:
            print(f"\n--- Channel: {name} ---")
            try:
                messages = self.get_channel_messages(team_id, channel_id, messages_per_channel)
                for msg in messages:
                    user = msg.get("from", {}).get("user")
                    if not user or not msg.get("body", {}).get("content"):
                        continue
                    sender = user.get("displayName", "Unknown")
                    body = self._strip_html(msg["body"]["content"])[:120]
                    time = self._format_time(msg.get("createdDateTime", ""))
                    if body:
                        print(f"  [{time}] {sender}: {body}")
            except requests.HTTPError as e:
                print(f"  Error: {e}")

    def print_mail(self, limit: int = 5):
        """Print recent mail messages."""
        for msg in self.get_mail(limit):
            sender = msg.get("from", {}).get("emailAddress", {}).get("name", "Unknown")
            subject = msg.get("subject", "(no subject)")[:80]
            time = self._format_time(msg.get("receivedDateTime", ""))
            print(f"  [{time}] {sender}: {subject}")


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Test Microsoft Graph API client")
    parser.add_argument("--mail", action="store_true", help="Fetch and display recent mail (requires browser auth)")
    parser.add_argument("--top", type=int, default=10, help="Number of items to fetch (default: 10)")
    parser.add_argument("--chats", action="store_true", help="Fetch and display recent chats (requires browser auth)")
    parser.add_argument("--teams", action="store_true", help="List joined teams (uses Azure CLI)")
    parser.add_argument("--me", action="store_true", help="Show current user info (uses Azure CLI)")
    parser.add_argument("--force-auth", action="store_true", help="Force re-authentication via browser")
    args = parser.parse_args()

    client = GraphClient()

    # Default to --mail if no action specified
    if not any([args.mail, args.chats, args.teams, args.me]):
        args.mail = True

    # Determine if we need WorkIQ token (browser auth) for mail/chats
    needs_workiq_token = args.mail or args.chats

    if needs_workiq_token:
        if args.force_auth or not client.access_token:
            print("Mail/Chats require browser authentication (WorkIQ token)...")
            if not client.authenticate(force=args.force_auth):
                print("Authentication failed!")
                exit(1)
        else:
            # Validate existing token
            if not client.authenticate():
                print("Authentication failed!")
                exit(1)

    if args.me:
        print("\n=== Current User (via Azure CLI) ===")
        if not client.az_token:
            print("  Error: Azure CLI not authenticated. Run: az login")
        else:
            try:
                me = client.get_me()
                print(f"  Name: {me.get('displayName', 'N/A')}")
                print(f"  Email: {me.get('mail', me.get('userPrincipalName', 'N/A'))}")
                print(f"  ID: {me.get('id', 'N/A')}")
            except Exception as e:
                print(f"  Error: {e}")

    if args.mail:
        print(f"\n=== Recent Mail (top {args.top}) ===")
        try:
            messages = client.get_mail(top=args.top)
            if not messages:
                print("  No messages found.")
            for msg in messages:
                sender = msg.get("from", {}).get("emailAddress", {}).get("name", "Unknown")
                sender_email = msg.get("from", {}).get("emailAddress", {}).get("address", "")
                subject = msg.get("subject", "(no subject)")[:80]
                time = GraphClient._format_time(msg.get("receivedDateTime", ""))
                preview = msg.get("bodyPreview", "")[:100]
                print(f"\n  [{time}] From: {sender} <{sender_email}>")
                print(f"    Subject: {subject}")
                if preview:
                    print(f"    Preview: {preview}...")
        except Exception as e:
            print(f"  Error fetching mail: {e}")

    if args.chats:
        print(f"\n=== Recent Chats (top {args.top}) ===")
        try:
            chats = client.get_chats(top=args.top)
            if not chats:
                print("  No chats found.")
            for chat in chats:
                chat_type = chat.get("chatType", "unknown")
                topic = chat.get("topic") or "(no topic)"
                members = [m.get("displayName", "?") for m in chat.get("members", [])]
                print(f"\n  Type: {chat_type}")
                print(f"    Topic: {topic}")
                print(f"    Members: {', '.join(members[:5])}{' ...' if len(members) > 5 else ''}")
        except Exception as e:
            print(f"  Error fetching chats: {e}")

    if args.teams:
        print("\n=== Joined Teams (via Azure CLI) ===")
        if not client.az_token:
            print("  Error: Azure CLI not authenticated. Run: az login")
        else:
            try:
                teams = client.get_joined_teams()
                if not teams:
                    print("  No teams found.")
                for team in teams:
                    print(f"  - {team.get('displayName', 'Unknown')} (ID: {team.get('id', 'N/A')[:8]}...)")
            except Exception as e:
                print(f"  Error fetching teams: {e}")

    print("\nDone.")



