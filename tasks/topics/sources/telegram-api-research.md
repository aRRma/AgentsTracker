# Telegram topics research (Bot API 10.3, 24.08.2026 is latest seen)

Library: Telegram.Bot 22.10.3 (nuspec: Bot API 10.3). Its XML docs contain CreateForumTopic, EditForumTopic, CloseForumTopic, DeleteForumTopic, UnpinAllForumTopicMessages, SendMessageDraft, SendRichMessageDraft, User.HasTopicsEnabled, User.AllowsUsersToCreateTopics, ForumTopic*.IsNameImplicit, Message.MessageThreadId/IsTopicMessage, BotCommandScopeChat. No upgrade needed.
SendChatAction ext signature: (chatId, action, int? messageThreadId, string? businessConnectionId, ct). CreateForumTopic(chatId, name, int? iconColor, string? iconCustomEmojiId, ct).

## Changelog
- 6.3 (05.11.2022): topics in supergroups: Chat.is_forum, Message.is_topic_message/message_thread_id, can_manage_topics, ForumTopicCreated/Closed/Reopened, createForumTopic, editForumTopic, closeForumTopic, reopenForumTopic, deleteForumTopic, unpinAllForumTopicMessages, getForumTopicIconStickers, message_thread_id on send methods.
- 6.4 (30.12.2022): ForumTopicEdited, GeneralForumTopicHidden/Unhidden, General methods (edit/close/reopen/hide/unhideGeneralForumTopic), message_thread_id on sendChatAction.
- 6.8: unpinAllGeneralForumTopicMessages.
- 9.3 (31.12.2025): topics in PRIVATE chats: User.has_topics_enabled (getMe only), message_thread_id/is_topic_message for private chats, message_thread_id on send/copy/forward methods and sendChatAction, edit/deleteForumTopic and unpinAll... in private chats; is_name_implicit on ForumTopic/ForumTopicCreated; sendMessageDraft added.
- 9.4 (09.02.2026): createForumTopic works in private chats; User.allows_users_to_create_topics; BotFather Mini App setting to forbid users creating/deleting topics.
- 9.5 (01.03.2026): sendMessageDraft for all bots.
- 10.0 (08.05.2026): sendMessageDraft accepts empty text ("Thinking..."). 10.1: sendRichMessageDraft. 10.3: can_stop, keep_on_stop on drafts.

## 1. Supergroup forums
- createForumTopic: name 1-128 chars; icon_color one of 7322096, 16766590, 13338331, 9367192, 16749490, 16478047; icon_custom_emoji_id optional (ids from getForumTopicIconStickers). Needs can_manage_topics (supergroup).
- editForumTopic: name 0-128 (empty = keep), icon_custom_emoji_id ("" removes). can_manage_topics unless bot created the topic.
- close/reopenForumTopic: can_manage_topics unless bot is creator.
- deleteForumTopic: deletes topic with all messages; needs can_delete_messages (no creator exception stated).
- unpinAllForumTopicMessages: can_pin_messages.
- General topic: edit(name 1-128)/close/reopen/hide/unhide/unpinAll...; can_manage_topics (unpin: can_pin_messages). General topic has no message_thread_id (messages in it come without thread id / id 1 for sending).
- Service messages: forum_topic_created/edited/closed/reopened, general_forum_topic_hidden/unhidden. No forum_topic_deleted exists in changelog: deletion by a user gives NO update to the bot (confirmed only by absence in changelog/Message fields; not tested live).
- Message.message_thread_id: "for supergroups and private chats only". is_topic_message true if in topic.
- Topic count limit: UNCONFIRMED (not on the pages read).
- Rate limit (FAQ): group max 20 msgs/min; private chat ~1 msg/sec per chat (bursts ok, then 429); ~30 msgs/sec bulk.

## 2. Private-chat topics (key)
- Needs BotFather enable ("Private-chat topics need to be enabled for the bot via @BotFather"; exact menu UNCONFIRMED; features page says only this). User.has_topics_enabled (from getMe) shows it.
- Since 9.4 the bot can createForumTopic(chat_id = user id), edit/delete topics, unpinAll. close/reopen docs say "forum supergroup" only -> in private chats UNCONFIRMED.
- User can create/delete topics unless BotFather setting forbids; User.allows_users_to_create_topics reports it. So user-deleted topics are possible: bot learns only by 400 error on send (error text UNCONFIRMED, probably "message thread not found").
- No admin rights concept in private chats.
- Client support: UNCONFIRMED which client versions show it.
- Replaces the need for a group: yes for single user; privacy mode is moot (private chat gets all messages); rate limit per chat ~1/s instead of 20/min per group.

## 3. Privacy mode (features page)
- Default on for groups; bot sees only relevant messages/commands; (from earlier knowledge, not on fetched text: commands to the bot, replies to the bot's messages, @mentions). Admin bots receive ALL messages. Service messages always. Private chats all messages. Changing needs re-adding bot to group.
- In groups a command for this bot looks /cmd@bot_name; the update does not carry the scope. Scopes: BotCommandScopeDefault, AllPrivateChats, AllGroupChats, AllChatAdministrators, Chat, ChatAdministrators, ChatMember (definitions not fetched, from memory). Always validate commands server-side.

## 4. Callbacks and drafts
- CallbackQuery.message is MaybeInaccessibleMessage: Message (has message_thread_id) or InaccessibleMessage (only chat, message_id, date - NO thread id). So in topics, the thread id must be taken from Message when accessible; otherwise keep (chat,message)->thread mapping or encode in callback data.
- sendMessageDraft: chat_id "target private chat" (private only!), message_thread_id optional (so works in private-chat topics), draft_id nonzero, text 0-4096, parse_mode/entities, can_stop, keep_on_stop; draft is a 30 s preview, must send final sendMessage. Does not work in groups.
- sendRichMessageDraft (10.1) also has message_thread_id.

## 5. Repo notes
Telegram.Bot 22.10.3 (src/AgentsTracker.Channels.Telegram/AgentsTracker.Channels.Telegram.csproj).
TelegramChannel.cs: chat identity is only TelegramChatId(long) (TelegramIds.cs) -> no thread anywhere:
- ToIncoming (~l.277): chat = message.Chat.Id; ignores message.MessageThreadId.
- ToPress (~l.318): chat from callback.Message?.Chat; no thread.
- SendAsync (l.331), SendFileAsync/SendMedia (l.367-391), Reply() (l.398), EditAsync (~l.459), delete (l.472), IndicateTypingAsync (l.492, SendChatAction without thread id): none pass messageThreadId.
- Kind(chat): non-private => ChatKind.Group; ButtonPress/ChatDispatcher admit only private chats - a topic in a private chat stays ChatType.Private, so Direct check still passes.
- ReceiverOptions.AllowedUpdates = Message, CallbackQuery (l.122); enough for topics (service messages arrive as Message).
- Simplest design: make TelegramChatId carry optional thread id (key "chat:thread"), or add thread to ChatId; ParseChat/TelegramIds.TryParse need update.
