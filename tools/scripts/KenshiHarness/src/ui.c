// The game's GUI as text: MyGUI's widget tree walked through MyGUIEngine's exports, each widget with its class (from RTTI),
// caption, name and the part of it that shows on screen, so a widget can be found and clicked by what it says.
//
// MyGUI's own child access (getChildAt) leaves out the parts a widget's skin makes: a window's caption bar and close
// button, a tab control's tab buttons, a combo box's drop-down list. The walk reads the two child vectors themselves
// (Widget::mWidgetChild and, right after it, mWidgetChildSkin; MyGUI 3.2 Widget.h), whose place in a Widget is found at
// run time by looking for the vector that holds what getChildAt returns. A skin's parts nest (a window's skin is a frame
// holding the caption, the buttons and the client area), so everything under a skin part is a skin part too, down to the
// owner's client area, whose children are the window's content again.
#include "harness.h"
#include <stdio.h>
#include <string.h>

// ---- MyGUI --------------------------------------------------------------------------------------------------------------

// std::vector<Widget*> (VC++ 2010, release: three pointers and the allocator, 32 bytes) and MyGUI::Enumerator over one: a
// flag and the vector's iterators.
typedef struct { void** begin; void** end; void** capacity; void* allocator; } WidgetVector;
typedef struct { char first; void** current; void** end; } Enumerator;

// MyGUI::WidgetStyle: a Child is cropped by its parent, a Popup or Overlapped widget is not.
typedef struct { int value; } WidgetStyle;

static void* (*getGui)(void);
static Enumerator* (*getRoots)(const void* gui, Enumerator* out);
static size_t (*getChildCount)(void* widget);
static void* (*getChildAt)(void* widget, size_t index);
static void* (*getClient)(void* widget);
static const GameString* (*getName)(const void* widget);
static char (*getVisible)(const void* widget);
static char (*getInheritedEnabled)(const void* widget);
static char (*getSelected)(const void* button);
static WidgetStyle* (*getStyle)(const void* widget, WidgetStyle* out);
static int (*getLeft)(const void* rectangle);
static int (*getTop)(const void* rectangle);
static int (*getWidth)(const void* rectangle);
static int (*getHeight)(const void* rectangle);
static const GameString* (*getUserString)(const void* userData, const GameString* key);
static const GameString* (*toUtf8)(const void* ustring);

// The slot of the virtual TextBox::getCaption, which EditBox, Window and others override.
static int captionSlot = -1;

// Where Widget::mWidgetChild is in a Widget, once found; mWidgetChildSkin follows it.
static int childrenOffset = -1;

int ui_init(void)
{
    const wchar_t* gui = L"MyGUIEngine_x64.dll";

    getGui = sym(gui, "?getInstancePtr@?$Singleton@VGui@MyGUI@@@MyGUI@@SAPEAVGui@2@XZ");
    getRoots = sym(gui, "?getEnumerator@Gui@MyGUI@@QEBA?AV?$Enumerator@V?$vector@PEAVWidget@MyGUI@@V?$allocator@PEAVWidget@MyGUI@@@std@@@std@@@2@XZ");
    getChildCount = sym(gui, "?getChildCount@Widget@MyGUI@@QEAA_KXZ");
    getChildAt = sym(gui, "?getChildAt@Widget@MyGUI@@QEAAPEAV12@_K@Z");
    getClient = sym(gui, "?getClientWidget@Widget@MyGUI@@QEAAPEAV12@XZ");
    getName = sym(gui, "?getName@Widget@MyGUI@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
    getVisible = sym(gui, "?getVisible@Widget@MyGUI@@QEBA_NXZ");
    getInheritedEnabled = sym(gui, "?getInheritedEnabled@Widget@MyGUI@@QEBA_NXZ");
    getSelected = sym(gui, "?getStateSelected@Button@MyGUI@@QEBA_NXZ");
    getStyle = sym(gui, "?getWidgetStyle@Widget@MyGUI@@QEBA?AUWidgetStyle@2@XZ");
    getLeft = sym(gui, "?getAbsoluteLeft@ICroppedRectangle@MyGUI@@QEBAHXZ");
    getTop = sym(gui, "?getAbsoluteTop@ICroppedRectangle@MyGUI@@QEBAHXZ");
    getWidth = sym(gui, "?getWidth@ICroppedRectangle@MyGUI@@QEBAHXZ");
    getHeight = sym(gui, "?getHeight@ICroppedRectangle@MyGUI@@QEBAHXZ");
    getUserString = sym(gui, "?getUserString@UserData@MyGUI@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@AEBV34@@Z");
    toUtf8 = sym(gui, "?asUTF8@UString@MyGUI@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");

    void** textBox = sym(gui, "??_7TextBox@MyGUI@@6BIObject@1@@");
    void* getCaption = sym(gui, "?getCaption@TextBox@MyGUI@@UEAAAEBVUString@2@XZ");
    if (textBox && getCaption) captionSlot = vtable_slot(textBox, getCaption, 96);

    return getGui && getRoots && getChildCount && getChildAt && getClient && getName && getVisible && getInheritedEnabled && getSelected
        && getStyle && getLeft && getTop && getWidth && getHeight && getUserString && toUtf8 && captionSlot >= 0;
}

// ---- A widget's children ------------------------------------------------------------------------------------------------

static int is_widget_vector(const WidgetVector* v)
{
    if (v->begin > v->end || v->end > v->capacity || (v->end - v->begin) > 4096) return 0;

    return v->begin == v->end || readable(v->begin, (size_t)((char*)v->end - (char*)v->begin));
}

// Looks in `widget` for the vector holding exactly the children getChildAt gives. A widget with a client part answers
// getChildAt with the client's children, so it finds nothing and the next widget is tried.
static void find_children_offset(void* widget)
{
    size_t count = getChildCount(widget);
    if (count == 0) return;

    void* first = getChildAt(widget, 0);

    for (int offset = 0x40; offset < 0x400; offset += 8)
    {
        const WidgetVector* v = (const WidgetVector*)((char*)widget + offset);
        if (!readable(v, sizeof *v) || !is_widget_vector(v)) continue;
        if ((size_t)(v->end - v->begin) != count || v->begin[0] != first) continue;

        childrenOffset = offset;
        return;
    }
}

// ---- One widget as the harness shows it ---------------------------------------------------------------------------------

typedef struct { int left, top, right, bottom; } Rect;

typedef struct
{
    void* widget;
    int depth;
    int root;
    int skin;
    char type[64];
    char caption[200];
    char name[96];
    Rect shown;
    int width, height;
    int enabled;
    int selected;
} Seen;

static Rect intersect(Rect a, Rect b)
{
    Rect r = {a.left > b.left ? a.left : b.left, a.top > b.top ? a.top : b.top, a.right < b.right ? a.right : b.right,
              a.bottom < b.bottom ? a.bottom : b.bottom};
    return r;
}

static int is_empty(Rect r) { return r.right <= r.left || r.bottom <= r.top; }

// Copies `text` without MyGUI's colour tags (#RRGGBB; ## is a literal #) and with line breaks as spaces.
static void plain_text(const char* text, size_t length, char* out, size_t size)
{
    size_t used = 0;

    for (size_t i = 0; i < length && used + 1 < size; i++)
    {
        if (text[i] == '#' && i + 1 < length && text[i + 1] == '#') i++;
        else if (text[i] == '#' && i + 6 < length)
        {
            i += 6;
            continue;
        }

        out[used++] = text[i] == '\n' || text[i] == '\r' ? ' ' : text[i];
    }

    out[used] = 0;
}

// The caption, or for a captionless skin button what its skin says it does ("[Close]" on a window's close button).
static void read_caption(void* widget, Seen* seen)
{
    seen->caption[0] = 0;

    if (rtti_base_offset(widget, ".?AVTextBox@MyGUI@@") == 0)
    {
        const void* (*getCaption)(void*) = VIRTUAL(widget, captionSlot);
        const GameString* utf8 = toUtf8(getCaption(widget));
        plain_text(gs_text(utf8), utf8->size, seen->caption, sizeof seen->caption);
    }

    int userData = rtti_base_offset(widget, ".?AVUserData@MyGUI@@");
    if (seen->caption[0] || !seen->skin || userData < 0) return;

    GameString key = gs_view("Event");
    const GameString* event = getUserString((char*)widget + userData, &key);
    if (event->size) snprintf(seen->caption, sizeof seen->caption, "[%.*s]", (int)event->size, gs_text(event));
}

static void read_widget(void* widget, int depth, int root, int skin, Rect clip, Seen* seen)
{
    const char* mangled = rtti_name(widget);
    int rectangle = rtti_base_offset(widget, ".?AVICroppedRectangle@MyGUI@@");
    const char* at = (const char*)widget + (rectangle > 0 ? rectangle : 0);
    const GameString* name = getName(widget);
    Rect whole = {getLeft(at), getTop(at), getLeft(at) + getWidth(at), getTop(at) + getHeight(at)};

    seen->widget = widget;
    seen->depth = depth;
    seen->root = root;
    seen->skin = skin;
    rtti_plain_name(mangled ? mangled : "?", seen->type, sizeof seen->type);
    read_caption(widget, seen);
    snprintf(seen->name, sizeof seen->name, "%.*s", (int)name->size, gs_text(name));

    seen->shown = intersect(whole, clip);
    seen->width = whole.right - whole.left;
    seen->height = whole.bottom - whole.top;
    seen->enabled = getInheritedEnabled(widget);
    seen->selected = rtti_base_offset(widget, ".?AVButton@MyGUI@@") == 0 && getSelected(widget);
}

// ---- The walk: every widget under the GUI's roots, hidden subtrees skipped unless asked for -----------------------------

// Called for each widget; returns whether the widget counts as a level for the ones under it (`depth`).
typedef int (*Visit)(const Seen* seen, void* context);

typedef struct { int all; Rect screen; Visit visit; void* context; } Walk;

// Where a widget is in the tree: how many counted widgets are above it, whether it is a skin part, the client area of
// the widget whose skin it belongs to, and the rectangle its parents leave of it.
typedef struct { int depth; int root; int skin; void* ownerClient; Rect clip; } Place;

static void walk(const Walk* w, void* widget, Place place);

static void walk_vector(const Walk* w, const WidgetVector* children, Place place)
{
    if (!readable(children, sizeof *children) || !is_widget_vector(children)) return;

    for (void** at = children->begin; at < children->end; at++)
    {
        walk(w, *at, place);
    }
}

// MyGUI's own child access, used until the child vectors are found: it leaves the skin's parts out.
static void walk_listed(const Walk* w, void* widget, Place under)
{
    size_t children = getChildCount(widget);

    for (size_t i = 0; i < children && i < 4096; i++)
    {
        walk(w, getChildAt(widget, i), under);
    }
}

static void walk(const Walk* w, void* widget, Place place)
{
    if (!readable(widget, sizeof(void*)) || place.depth > 48) return;
    if (!w->all && !getVisible(widget)) return;

    // A popup (a combo box's list) is placed on the screen, not inside its parent.
    WidgetStyle style;
    if (getStyle(widget, &style)->value != 0) place.clip = w->screen;

    Seen seen;
    read_widget(widget, place.depth, place.root, place.skin, place.clip, &seen);
    if (!w->all && is_empty(seen.shown)) return;

    int counted = w->visit(&seen, w->context);
    Place under = {place.depth + (counted ? 1 : 0), 0, 0, NULL, seen.shown};

    if (childrenOffset < 0) find_children_offset(widget);
    if (childrenOffset < 0)
    {
        walk_listed(w, widget, under);
        return;
    }

    // The widget's own skin, then its children: skin parts too when the widget is one, unless it is the client area.
    const WidgetVector* own = (const WidgetVector*)((char*)widget + childrenOffset);
    Place skin = under;
    skin.skin = 1;
    skin.ownerClient = getClient(widget);
    walk_vector(w, own + 1, skin);

    under.skin = place.skin && widget != place.ownerClient;
    under.ownerClient = under.skin ? place.ownerClient : NULL;
    walk_vector(w, own, under);
}

static int walk_all(int all, Visit visit, void* context)
{
    void* gui = getGui ? getGui() : NULL;
    Walk w = {all, {0, 0, 0, 0}, visit, context};
    if (!gui || !screen_size(&w.screen.right, &w.screen.bottom)) return 0;

    Enumerator roots;
    getRoots(gui, &roots);
    Place top = {0, 1, 0, NULL, w.screen};

    for (void** at = roots.current; at < roots.end; at++)
    {
        walk(&w, *at, top);
    }

    return 1;
}

// ---- ui [filter] [--all]: the tree as indented lines --------------------------------------------------------------------

typedef struct { Text* out; const char* filter; int all; } Listing;

static int contains(const char* text, const char* part)
{
    size_t length = strlen(part);

    for (; *text; text++)
    {
        if (_strnicmp(text, part, length) == 0) return 1;
    }

    return length == 0;
}

static int is_type(const Seen* s, const char* type) { return strcmp(s->type, type) == 0; }

// A line is worth showing when it says something or can be worked. A skin part only when it has a caption of its own
// (a tab's button, "[close]"): its Client and Caption parts repeat their owner's text. Plain panels, pictures and empty
// text boxes are left out.
static int worth_a_line(const Seen* s)
{
    if (s->skin) return s->caption[0] && strcmp(s->name, "Client") != 0 && strcmp(s->name, "Caption") != 0;
    if (s->caption[0]) return 1;
    if (is_type(s, "Widget") || is_type(s, "ImageBox") || is_type(s, "TextBox")) return s->root;

    return !is_type(s, "EditBox") || s->name[0];
}

static int list_widget(const Seen* s, void* context)
{
    Listing* l = context;
    if (!l->all && !worth_a_line(s)) return 0;
    if (l->filter && !contains(s->caption, l->filter) && !contains(s->name, l->filter) && !contains(s->type, l->filter)) return 1;

    text_add(l->out, "%*s%s", l->filter ? 0 : s->depth * 2, "", s->type);
    if (s->caption[0]) text_add(l->out, " \"%s\"", s->caption);
    if (s->name[0]) text_add(l->out, " name=%s", s->name);

    text_add(l->out, " @%d,%d %dx%d", (s->shown.left + s->shown.right) / 2, (s->shown.top + s->shown.bottom) / 2, s->width, s->height);
    if (s->selected) text_add(l->out, " selected");
    if (!s->enabled) text_add(l->out, " disabled");
    if (is_empty(s->shown)) text_add(l->out, " hidden");
    if (l->all && s->skin) text_add(l->out, " skin");
    text_add(l->out, "\n");
    return 1;
}

// Each line: the class, the caption, the name, "@x,y" (the middle of the part that shows, where a click lands) and the
// widget's size. Widgets that are hidden or scrolled out of view are left out without --all.
int cmd_ui(Command* c)
{
    Listing listing = {&c->reply, arg(c, 0, NULL), option(c, "--all")};
    if (!walk_all(listing.all, list_widget, &listing)) return fail(c, "the GUI is not up yet");

    if (c->reply.length == 0) text_add(&c->reply, "(no widgets%s)\n", listing.filter ? " match" : "");
    return 1;
}

// ---- Finding a widget by what it says -----------------------------------------------------------------------------------

#define MAX_MATCHES 12

typedef struct { const char* text; int exact; int count; Seen found[MAX_MATCHES]; } Search;

static int match_widget(const Seen* s, void* context)
{
    Search* search = context;
    if (!worth_a_line(s)) return 0;

    int hit = search->exact
        ? _stricmp(s->caption, search->text) == 0 || _stricmp(s->name, search->text) == 0
        : contains(s->caption, search->text) || contains(s->name, search->text);
    if (!hit) return 1;

    if (search->count < MAX_MATCHES) search->found[search->count] = *s;
    search->count++;
    return 1;
}

static void describe_matches(const Search* search, Text* why)
{
    text_add(why, "%d widgets match '%s'; pass --nth=<n> (from 0) or click the coordinates:\n", search->count, search->text);

    for (int i = 0; i < search->count && i < MAX_MATCHES; i++)
    {
        const Seen* s = &search->found[i];
        text_add(why, "  %d: %s \"%s\" name=%s @%d,%d\n", i, s->type, s->caption, s->name, (s->shown.left + s->shown.right) / 2,
                 (s->shown.top + s->shown.bottom) / 2);
    }
}

// A whole caption or name wins over a part of one, so "Spawn" finds the button and not "Spawn method".
int ui_locate(const char* text, int nth, int* x, int* y, Text* why)
{
    Search search = {text, 1, 0, {{0}}};
    if (!walk_all(0, match_widget, &search))
    {
        text_add(why, "the GUI is not up yet");
        return 0;
    }

    if (search.count == 0)
    {
        search.exact = 0;
        walk_all(0, match_widget, &search);
    }

    if (search.count == 0)
    {
        text_add(why, "no widget in view says or is named '%s' (see: ui)", text);
        return 0;
    }

    if ((search.count > 1 && nth < 0) || nth >= search.count || nth >= MAX_MATCHES)
    {
        describe_matches(&search, why);
        return 0;
    }

    const Seen* s = &search.found[nth < 0 ? 0 : nth];
    *x = (s->shown.left + s->shown.right) / 2;
    *y = (s->shown.top + s->shown.bottom) / 2;
    return 1;
}
