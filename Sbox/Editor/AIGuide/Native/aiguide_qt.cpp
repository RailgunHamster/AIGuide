// Project-local Qt 5 adapter. No Hammer binaries or engine sources are modified.
// Build with the Qt 5.15.2 MSVC x64 SDK matching s&box's shipped Qt libraries.
#define NOMINMAX
#include <Windows.h>
#include <QtWidgets/qwidget.h>
#include <QtWidgets/qmainwindow.h>
#include <QtWidgets/qgraphicsitem.h>
#include <QtWidgets/qdockwidget.h>
#include <QtWidgets/QtWidgets>
#include <QtCore/QtCore>
#include <QtGui/QtGui>
#include <cstdlib>
#include <stdexcept>
#include <functional>
#include <limits>
#include <future>
#include <memory>

namespace {
HWND dispatchWindow=nullptr;
constexpr UINT dispatchMessage=WM_APP+174;
LRESULT CALLBACK dispatchProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
    if(message==dispatchMessage){std::unique_ptr<std::function<void()>> work(reinterpret_cast<std::function<void()>*>(lparam));(*work)();return 0;}
    return DefWindowProcW(window,message,wparam,lparam);
}
void ensureDispatchWindow(){
    if(dispatchWindow)return;WNDCLASSW wc{};wc.lpfnWndProc=dispatchProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"MoonShooter.AIGuide.Dispatch";
    RegisterClassW(&wc);dispatchWindow=CreateWindowExW(0,wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
    if(!dispatchWindow)throw std::runtime_error("Could not create AI Guide GUI dispatcher");
}
void onGui(std::function<void()> work){
    if(QThread::currentThread()==qApp->thread()){ensureDispatchWindow();work();return;}
    if(!dispatchWindow)QMetaObject::invokeMethod(qApp,[]{ensureDispatchWindow();},Qt::BlockingQueuedConnection);
    auto done=std::make_shared<std::promise<void>>();auto future=done->get_future();
    auto task=new std::function<void()>([work=std::move(work),done]{try{work();done->set_value();}catch(...){done->set_exception(std::current_exception());}});
    if(!PostMessageW(dispatchWindow,dispatchMessage,0,reinterpret_cast<LPARAM>(task))){delete task;throw std::runtime_error("Could not post AI Guide GUI task");}
    future.get();
}
QString str(const QJsonObject& o, const char* k, QString d={}) {return o.value(k).isString()?o.value(k).toString():d;}
int num(const QJsonObject& o,const char*k,int d=0){return o.contains(k)?o.value(k).toInt(d):d;}
bool yes(const QJsonObject&o,const char*k,bool d=false){return o.contains(k)?o.value(k).toBool(d):d;}
QJsonArray rectJson(QRect r){return {r.x(),r.y(),r.width(),r.height()};}
QRect readRect(QJsonArray a){return {a[0].toInt(),a[1].toInt(),a[2].toInt(),a[3].toInt()};}
QJsonValue validUnicode(QJsonValue value){
    // Qt permits lone UTF-16 surrogates (some native icon labels contain them).
    // System.Text.Json rejects them; round-trip strings through UTF-8 at the boundary.
    if(value.isString())return QString::fromUtf8(value.toString().toUtf8());
    if(value.isArray()){QJsonArray out;for(auto v:value.toArray())out.append(validUnicode(v));return out;}
    if(value.isObject()){QJsonObject out;auto object=value.toObject();for(auto it=object.begin();it!=object.end();++it)out[QString::fromUtf8(it.key().toUtf8())]=validUnicode(it.value());return out;}
    return value;
}
QPair<int,Qt::KeyboardModifiers> parseKey(QString text){
    auto parts=text.trimmed().split('+');Qt::KeyboardModifiers mods;int value=0;
    for(auto part:parts){auto p=part.trimmed().toLower();if(p=="ctrl"||p=="control")mods|=Qt::ControlModifier;else if(p=="shift")mods|=Qt::ShiftModifier;else if(p=="alt")mods|=Qt::AltModifier;else if(p=="meta"||p=="win"||p=="super")mods|=Qt::MetaModifier;
        else if(p.size()==1)value=p[0].toUpper().unicode();
        else if(p.startsWith('f')&&p.mid(1).toInt()>=1&&p.mid(1).toInt()<=35)value=Qt::Key_F1+p.mid(1).toInt()-1;
        else {static QHash<QString,int> names{{"enter",Qt::Key_Return},{"return",Qt::Key_Return},{"escape",Qt::Key_Escape},{"esc",Qt::Key_Escape},{"tab",Qt::Key_Tab},{"space",Qt::Key_Space},{"backspace",Qt::Key_Backspace},{"delete",Qt::Key_Delete},{"del",Qt::Key_Delete},{"insert",Qt::Key_Insert},{"home",Qt::Key_Home},{"end",Qt::Key_End},{"pageup",Qt::Key_PageUp},{"pgup",Qt::Key_PageUp},{"pagedown",Qt::Key_PageDown},{"pgdown",Qt::Key_PageDown},{"left",Qt::Key_Left},{"right",Qt::Key_Right},{"up",Qt::Key_Up},{"down",Qt::Key_Down}};value=names.value(p,0);if(!value)throw std::runtime_error("Unsupported key name");}}
    if(!value)throw std::runtime_error("A key name is required");return {value,mods};
}
QString textOf(QWidget*w){
    for(auto key:{"text","title","windowTitle","currentText","placeholderText"}){auto v=w->property(key);if(v.isValid()&&!v.toString().isEmpty())return v.toString();}
    if(auto e=qobject_cast<QPlainTextEdit*>(w))return e->toPlainText().left(160);
    if(auto e=qobject_cast<QTextEdit*>(w))return e->toPlainText().left(160);
    return {};
}
bool own(QWidget*w){for(auto p=w;p;p=p->parentWidget())if(p->property("aiguideOwn").toBool())return true;return false;}
QString propertyOf(QWidget*w){for(auto p=w;p;p=p->parentWidget())for(auto key:{"aiguideProperty","propertyName","property"})if(p->property(key).isValid()&&!p->property(key).toString().isEmpty())return p->property(key).toString();return {};}
QString valueOf(QWidget*w){for(auto p=w;p;p=p->parentWidget())if(p->property("aiguideValue").isValid())return p->property("aiguideValue").toString();return w->property("value").toString();}
QString uiLabel(QString text){return text.section('\t',0,0).replace(QChar(0x2026),"...").remove('&').trimmed();}
QString contentText(QString text){return text.replace('\\','/').remove(QRegularExpression("\\s+"));}
bool hasLabel(QWidget*w,QString wanted){for(auto label:w->findChildren<QLabel*>())if(uiLabel(label->text())==uiLabel(wanted))return true;return false;}
struct Element {
    QPointer<QWidget> w; QPersistentModelIndex index; QPointer<QAction> action;
    QString kind="control"; int item=-1; quintptr graphic=0;
    bool alive()const{
        if(!w)return false;if(kind=="tree")return index.isValid();if(kind=="menu")return !action.isNull();
        if(kind=="tab"){auto b=qobject_cast<QTabBar*>(w.data());return b&&item>=0&&item<b->count();}
        if(kind=="graphic"){auto v=qobject_cast<QGraphicsView*>(w.data());if(!v||!v->scene())return false;for(auto g:v->scene()->items())if(quintptr(g)==graphic)return true;return false;}
        return true;
    }
    QString label() const {
        if(!alive())return {};
        if(index.isValid())return index.data().toString();
        if(action)return action->text();
        if(kind=="tab")if(auto b=qobject_cast<QTabBar*>(w.data()))return b->tabText(item);
        if(kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w.data()))for(auto g:v->scene()->items())if(quintptr(g)==graphic){if(auto t=dynamic_cast<QGraphicsTextItem*>(g))return t->toPlainText();return g->toolTip();}
        return textOf(w);
    }
    QRect rect()const {
        if(!alive())return {};
        QRect r=w->rect(); QWidget*canvas=w;
        if(index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w.data())){r=v->visualRect(index);canvas=v->viewport();}
        if(action){if(auto m=qobject_cast<QMenu*>(w.data()))r=m->actionGeometry(action);if(auto m=qobject_cast<QMenuBar*>(w.data()))r=m->actionGeometry(action);}
        if(kind=="tab")if(auto b=qobject_cast<QTabBar*>(w.data()))r=b->tabRect(item);
        if(kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w.data())){r={};for(auto g:v->scene()->items())if(quintptr(g)==graphic){r=v->mapFromScene(g->sceneBoundingRect()).boundingRect();break;}canvas=v->viewport();}
        return {canvas->mapToGlobal(r.topLeft()),r.size()};
    }
    bool visible()const {
        if(!alive()||!w->isVisible()||rect().isEmpty())return false;
        if(action&&!action->isVisible())return false;
        auto r=rect();for(QWidget*p=w;p;p=p->parentWidget()){if(!p->isVisible())return false;r=r.intersected(QRect(p->mapToGlobal({0,0}),p->size()));if(r.isEmpty())return false;if(p->isWindow())break;}return true;
    }
};
class Overlay:public QWidget {
public:
    QVector<QPair<QRect,QString>> marks;QString caption;bool dim=false;
    int showCount=0,hideCount=0,paintCount=0;
    // Hammer's GPU canvas has native child windows. Painting a translucent child
    // over it invalidates/occludes that canvas; use a compositor-owned tool window.
    Overlay(QWidget*p):QWidget(p,Qt::Tool|Qt::FramelessWindowHint|Qt::WindowTransparentForInput|Qt::WindowDoesNotAcceptFocus){setProperty("aiguideOwn",true);setAttribute(Qt::WA_TransparentForMouseEvents);setAttribute(Qt::WA_ShowWithoutActivating);setAttribute(Qt::WA_TranslucentBackground);setFocusPolicy(Qt::NoFocus);}
    void showEvent(QShowEvent*e)override{++showCount;auto hwnd=reinterpret_cast<HWND>(internalWinId());if(hwnd)SetWindowLongPtrW(hwnd,GWL_EXSTYLE,GetWindowLongPtrW(hwnd,GWL_EXSTYLE)|WS_EX_NOACTIVATE|WS_EX_TRANSPARENT);QWidget::showEvent(e);}
    void hideEvent(QHideEvent*e)override{++hideCount;QWidget::hideEvent(e);}
    void draw(QPainter&p){
        p.setRenderHint(QPainter::Antialiasing);
        if(dim){QPainterPath path;path.addRect(rect());for(auto&m:marks)path.addRoundedRect(QRectF(mapFromGlobal(m.first.topLeft()),m.first.size()).adjusted(-4,-4,4,4),5,5);path.setFillRule(Qt::OddEvenFill);p.fillPath(path,QColor(0,0,0,115));}
        for(auto&m:marks){QRect r(mapFromGlobal(m.first.topLeft()),m.first.size());r=r.adjusted(-3,-3,3,3).intersected(rect().adjusted(3,3,-3,-3));if(r.isEmpty())continue;p.setPen(QPen(QColor("#ffb74d"),3));p.setBrush(Qt::NoBrush);p.drawRoundedRect(r,4,4);if(!m.second.isEmpty()){QRect tag(QPoint(qMax(3,r.left()),qMax(3,r.top()-23)),QSize(qMax(28,m.second.size()*9),22));p.fillRect(tag,QColor("#ffb74d"));p.setPen(Qt::black);p.drawText(tag,Qt::AlignCenter,m.second);}}
    }
    void paintEvent(QPaintEvent*)override{++paintCount;QPainter p(this);draw(p);}
};
class Bridge:public QObject {
public:
    QHash<QObject*,int> ids;QHash<QString,Element> refs;int seq=0;
    QSet<const QEvent*> synthetic;QJsonArray events,records,steps;QJsonObject context,lastGuide;QHash<QObject*,qint64> committedAt;
    QHash<QWidget*,QPointer<Overlay>> overlays;QPointer<QWidget>bubble;QPointer<QLabel>message;
    QJsonArray highlights;QString markText,title,state="idle";int step=0;bool dim=true; qint64 markUntil=0,entered=0,pendingClick=0,nextFileCheck=0,nextScroll=0;int scrollTries=0;bool preSatisfied=false,manualConfirmation=false,restartArmed=false,fallbackUsed=false;QString targetError;Element currentElement;
    // Per-step target cache. Resolving a query walks the editor UI tree; doing that several times per
    // 80 ms tick starved Hammer/material editor of GUI time (stalls, "not responding").
    int cachedStep=-1;qint64 cachedAt=0,nextCondition=0;Element cachedTarget;QPointer<QWidget>cachedScope;QString cachedError;bool cachedFallback=false;
    // Within one RPC or tick, identical queries resolve once (steps reuse their target in `until`).
    struct Memo{Element e;QString error;};QHash<QString,Memo> memo;int memoDepth=0;bool ticking=false;QVector<Element> highlightCache;qint64 highlightAt=0;qint64 lastClickTime=-1;QPoint lastClickPos;
    struct MemoScope{Bridge*b;explicit MemoScope(Bridge*bridge):b(bridge){if(b->memoDepth++==0)b->memo.clear();}~MemoScope(){if(--b->memoDepth==0)b->memo.clear();}};
    QTimer timer;bool replay=false;int eventSequence=0,actionSequence=0;QHash<int,QJsonObject> actionResults;
    QPointer<QWidget>fixture,secondFixture,fileFixture,hovered,presentedWindow;bool activationSucceeded=false,dialogOverride=false,previousDialogPolicy=false,rehearsing=false;
    Bridge(){qApp->installEventFilter(this);connect(&timer,&QTimer::timeout,this,[this]{tick();});timer.start(80);}
    ~Bridge(){clear();}
    QString ref(Element e){if(!e.w)return {};int id=ids.value(e.w,0);if(!id){id=++seq;ids[e.w]=id;connect(e.w,&QObject::destroyed,this,[this,obj=e.w.data()]{ids.remove(obj);committedAt.remove(obj);});}QString r="e"+QString::number(id);if(e.kind!="control"){r+='.'+e.kind+':';if(e.index.isValid()){QStringList rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(QString::number(i.row()));r+=rows.join(',')+':'+QString::number(e.index.column());}else if(e.action)r+=QString::number(quintptr(e.action.data()),16);else r+=QString::number(e.graphic?e.graphic:e.item);}refs[r]=e;return r;}
    QList<QWidget*> roots(){auto list=qApp->topLevelWidgets();list.erase(std::remove_if(list.begin(),list.end(),[](QWidget*w){return own(w)||w->windowType()==Qt::ToolTip;}),list.end());std::sort(list.begin(),list.end(),[](QWidget*a,QWidget*b){return (QString(a->metaObject()->className())+a->windowTitle())<(QString(b->metaObject()->className())+b->windowTitle());});return list;}
    bool classMatch(QWidget*w,const QJsonObject&q){auto c=str(q,"class");if(c.isEmpty())return true;QString name=w->metaObject()->className();if(yes(q,"exact")?name==c:name.contains(c,Qt::CaseInsensitive))return true;return w->inherits(c.toUtf8().constData())||w->managedClassNames.contains(c);}
    bool widgetMatches(QWidget*w,const QJsonObject&q){return (str(q,"name").isEmpty()||w->objectName()==str(q,"name"))&&classMatch(w,q);}
    // kinds: "any", "control" or one virtual kind. With a filter, virtual items are only enumerated for
    // widgets whose own class/name match; model rows of unrelated views (asset browsers) are never read.
    QVector<Element> elements(QWidget*root=nullptr,int maxItems=40,QString kinds="any",const QJsonObject*filter=nullptr){
        QVector<Element> out;QList<QWidget*> pending=root?QList<QWidget*>{root}:roots();QSet<QWidget*>seen;
        auto want=[&](const char*kind){return kinds=="any"||kinds==kind;};
        for(int n=0;n<pending.size()&&out.size()<20000;n++){
            QWidget*w=pending[n];if(!w||seen.contains(w)||own(w))continue;seen.insert(w);if(want("control"))out.append(Element{w});
            for(auto child:w->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly))pending.append(child);
            if(kinds=="control"||(filter&&!widgetMatches(w,*filter)))continue;
            if(auto v=qobject_cast<QAbstractItemView*>(w))if(want("tree")&&!qobject_cast<QHeaderView*>(w)&&v->model()){
                int count=0;std::function<void(QModelIndex,int)>visit=[&](QModelIndex parent,int depth){if(depth>16||count>=maxItems)return;for(int row=0;row<v->model()->rowCount(parent)&&count<maxItems;row++){for(int col=0;col<qMin(12,v->model()->columnCount(parent))&&count<maxItems;col++){auto ix=v->model()->index(row,col,parent);if(ix.isValid()){Element e;e.w=w;e.kind="tree";e.index=ix;out.append(e);count++;}}visit(v->model()->index(row,0,parent),depth+1);}};visit({},0);
            }
            if(auto b=qobject_cast<QTabBar*>(w))if(want("tab"))for(int i=0;i<qMin(b->count(),maxItems);i++){Element e;e.w=w;e.kind="tab";e.item=i;out.append(e);}
            if((qobject_cast<QMenu*>(w)||qobject_cast<QMenuBar*>(w))&&want("menu"))for(auto a:w->actions()){Element e;e.w=w;e.kind="menu";e.action=a;out.append(e);}
            if(auto v=qobject_cast<QGraphicsView*>(w))if(want("graphic")&&v->scene()){int count=0;for(auto g:v->scene()->items()){if(count++>=maxItems)break;Element e;e.w=w;e.kind="graphic";e.graphic=quintptr(g);out.append(e);}}
        }return out;
    }
    QJsonObject identity(QWidget*w){QJsonObject o{{"class",w->metaObject()->className()},{"name",w->objectName()}};int nth=0;auto siblings=w->parentWidget()?w->parentWidget()->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly):roots();for(auto s:siblings){if(s==w)break;if(QString(s->metaObject()->className())==QString(w->metaObject()->className())&&s->objectName()==w->objectName())nth++;}o["nth"]=nth;return o;}
    QString locator(Element e){QJsonArray path;for(auto p=e.w.data();p;p=p->parentWidget())path.prepend(identity(p));QJsonObject o{{"path",path},{"kind",e.kind},{"text",e.label()}};if(qobject_cast<QLineEdit*>(e.w.data())){auto semantic=fallbackFor(e);if(!semantic.isEmpty())o["semantic"]=semantic;}if(e.index.isValid()){QJsonArray rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(i.row());o["rows"]=rows;o["column"]=e.index.column();}if(e.kind=="tab")o["item"]=e.item;return "/qt/"+QJsonDocument(o).toJson(QJsonDocument::Compact).toBase64(QByteArray::Base64UrlEncoding|QByteArray::OmitTrailingEquals);}
    bool segment(QWidget*w,QJsonObject s){return QString(w->metaObject()->className())==str(s,"class")&&w->objectName()==str(s,"name")&&(!s.contains("text")||textOf(w).left(100)==str(s,"text"));}
    Element locate(QString value){auto doc=QJsonDocument::fromJson(QByteArray::fromBase64(value.mid(4).toLatin1(),QByteArray::Base64UrlEncoding));auto spec=doc.object();auto path=spec["path"].toArray();QWidget*w=nullptr;for(auto v:path){QList<QWidget*> candidates=w?w->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly):roots();auto s=v.toObject();int n=0;QWidget*next=nullptr;for(auto c:candidates)if(segment(c,s)&&n++==num(s,"nth")){next=c;break;}if(!next)return {};w=next;}if(!w)return {};QString kind=str(spec,"kind","control");if(kind=="control"){if(spec.contains("semantic")){try{if(resolve(spec["semantic"],true).w!=w)return {};}catch(...){return {};}}return Element{w};}QVector<Element>all;{QJsonObject self{{"class",QString(w->metaObject()->className())},{"name",w->objectName()},{"exact",true}};for(auto e:elements(w,2000,kind,&self))if(e.w==w)all.append(e);}QVector<Element>matches;for(auto e:all)if(e.w==w&&e.kind==kind&&e.label()==str(spec,"text"))matches.append(e);if(matches.size()==1)return matches[0];for(auto e:matches){if(kind=="tab"&&e.item==num(spec,"item"))return e;if(kind=="tree"){QJsonArray rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(i.row());if(rows==spec["rows"].toArray()&&e.index.column()==num(spec,"column"))return e;}}return {};}
    bool match(const Element&e,const QJsonObject&q){
        if(!e.w)return false;auto kind=str(q,"kind","any");if(kind!="any"&&e.kind!=kind)return false;
        if(!widgetMatches(e.w,q))return false;
        bool exact=yes(q,"exact");auto contains=[&](const QString&have,const QString&want){return want.isEmpty()||(exact?have==want:have.contains(want,Qt::CaseInsensitive));};
        auto wanted=str(q,"text").trimmed();if(!wanted.isEmpty()){auto label=e.label().trimmed();if(e.kind=="menu"){label=uiLabel(label);wanted=uiLabel(wanted);}if(!contains(label,wanted))return false;}
        if(!str(q,"tooltip").isEmpty()&&!contains(e.w->toolTip(),str(q,"tooltip")))return false;
        if(!str(q,"property").isEmpty()&&!contains(propertyOf(e.w),str(q,"property")))return false;
        if(!str(q,"label").isEmpty()&&!hasLabel(e.w,str(q,"label")))return false;
        return !yes(q,"visible_only",true)||e.visible();
    }
    Element resolve(QJsonValue target,bool includeHidden=false){
        if(target.isString()){auto s=target.toString();if(s.startsWith("/qt/"))return locate(s);if(refs.contains(s)){auto e=refs.value(s);if(e.alive())return e;return {};}target=QJsonObject{{"text",s}};}
        if(!target.isObject())return {};
        auto q=target.toObject();if(includeHidden)q["visible_only"]=false;
        QString key;if(memoDepth>0){key=QString::fromUtf8(QJsonDocument(q).toJson(QJsonDocument::Compact));auto hit=memo.constFind(key);if(hit!=memo.constEnd()){if(!hit->error.isEmpty())throw std::runtime_error(hit->error.toStdString());if(!hit->e.w||hit->e.alive())return hit->e.w?hit->e:Element{};}}
        try{auto found=resolveQuery(q,includeHidden);if(memoDepth>0)memo.insert(key,Memo{found,{}});return found;}
        catch(const std::exception&ex){if(memoDepth>0)memo.insert(key,Memo{{},QString::fromUtf8(ex.what())});throw;}
    }
    Element resolveQuery(const QJsonObject&q,bool includeHidden){
        QWidget*under=nullptr;if(q.contains("under")){under=resolve(q["under"],includeHidden||!yes(q,"visible_only",true)).w;if(!under)return {};}
        QVector<Element>matches;for(auto e:elements(under,2000,str(q,"kind","any"),&q))if(match(e,q))matches.append(e);
        // Keep offscreen fields resolvable, while preferring the active document/tab.
        if(includeHidden&&matches.size()>1){QVector<Element>shown;for(auto e:matches)if(e.w->isVisible())shown.append(e);if(!shown.isEmpty())matches=shown;}
        if(matches.size()==1)return matches[0];
        if(matches.size()>1){QVector<Element>exact;for(auto e:matches)if(e.label().remove('&')==str(q,"text"))exact.append(e);if(exact.size()==1)return exact[0];throw std::runtime_error(QString("Ambiguous UI target (%1 matches); use ui_find then ref or locator").arg(matches.size()).toStdString());}return {};
    }
    Element stepTarget(QJsonObject s){
        targetError.clear();fallbackUsed=false;Element e;
        try{e=resolve(s["target"],true);}catch(const std::exception&ex){targetError=QString::fromUtf8(ex.what());}
        if(!e.w&&s.contains("fallback")){try{e=resolve(s["fallback"],true);fallbackUsed=bool(e.w);}catch(const std::exception&ex){targetError=QString::fromUtf8(ex.what());}}
        if(e.w)targetError.clear();else if(targetError.isEmpty())targetError="目标尚未出现";
        return e;
    }
    // Re-resolve a live cached target at most once a second, a missing one every 300 ms.
    Element targetFor(const QJsonObject&s,bool force=false){
        auto now=QDateTime::currentMSecsSinceEpoch();
        bool fresh=cachedStep==step&&!force&&(cachedTarget.alive()?now-cachedAt<1000:now-cachedAt<300);
        if(!fresh){MemoScope scope(this);cachedTarget=stepTarget(s);cachedError=targetError;cachedFallback=fallbackUsed;cachedScope=cachedTarget.w?cachedTarget.w->window():scopeWindow(s["target"]);cachedStep=step;cachedAt=now;}
        targetError=cachedError;fallbackUsed=cachedFallback;return cachedTarget;
    }
    void invalidateTarget(){cachedStep=-1;cachedTarget={};cachedScope=nullptr;nextCondition=0;}
    void scrollIntoView(Element e){
        if(e.index.isValid()){if(auto v=qobject_cast<QAbstractItemView*>(e.w.data()))v->scrollTo(e.index);return;}
        for(auto p=e.w->parentWidget();p;p=p->parentWidget())if(auto area=qobject_cast<QAbstractScrollArea*>(p)){
            auto at=area->viewport()->mapFromGlobal(e.rect().center());auto bounds=area->viewport()->rect();
            if(!bounds.contains(at)){area->verticalScrollBar()->setValue(area->verticalScrollBar()->value()+at.y()-bounds.center().y());area->horizontalScrollBar()->setValue(area->horizontalScrollBar()->value()+at.x()-bounds.center().x());}
        }
    }
    void dialogPolicy(){
        bool active=state=="running"||rehearsing;
        if(active&&!dialogOverride){previousDialogPolicy=QCoreApplication::testAttribute(Qt::AA_DontUseNativeDialogs);QCoreApplication::setAttribute(Qt::AA_DontUseNativeDialogs,true);dialogOverride=true;}
        else if(!active&&dialogOverride){QCoreApplication::setAttribute(Qt::AA_DontUseNativeDialogs,previousDialogPolicy);dialogOverride=false;}
    }
    QString projectFile(QString relative){auto root=QDir::cleanPath(QDir::fromNativeSeparators(str(context,"project")));auto path=QDir::cleanPath(QDir(root).absoluteFilePath(relative));if(root.isEmpty()||!path.startsWith(root+"/",Qt::CaseInsensitive))throw std::runtime_error("Tutorial project_path must stay inside the current project");return path;}
    QWidget*scopeWindow(QJsonValue target){if(!target.isObject())return nullptr;auto q=target.toObject();if(q.contains("under")){try{auto e=resolve(q["under"],true);if(e.w)return e.w->window();}catch(...){}return scopeWindow(q["under"]);}return nullptr;}
    void present(QWidget*window,bool force=false){
        if(!window||own(window)||(!force&&presentedWindow==window)||!yes(lastGuide,"activate_window",true))return;
        if(auto modal=qApp->activeModalWidget())if(modal!=window&&!modal->isAncestorOf(window))return;
        if(auto popup=qApp->activePopupWidget())if(popup!=window&&!window->isAncestorOf(popup))return;
        // Never show() a hidden window: targets resolve hidden menus/dialogs too, and resurrecting them
        // outside their owner's exec()/popup() flow leaves Hammer in states it does not expect.
        if(!window->isVisible()||qobject_cast<QMenu*>(window)||window->windowType()==Qt::Popup)return;
        if(window->isMinimized())window->showNormal();
        window->raise();window->activateWindow();
        auto hwnd=reinterpret_cast<HWND>(window->internalWinId());activationSucceeded=window->isActiveWindow();
        if(hwnd){SetForegroundWindow(hwnd);activationSucceeded=GetForegroundWindow()==hwnd;if(!activationSucceeded){FLASHWINFO info{sizeof(FLASHWINFO),hwnd,FLASHW_TRAY,2,0};FlashWindowEx(&info);}}
        presentedWindow=window;
        log({{"event","window_presented"},{"window",window->windowTitle()},{"active",activationSucceeded},{"index",step}});
    }
    QJsonObject fallbackFor(Element e){
        if(!e.w)return {};auto root=e.w->window();while(qobject_cast<QMenu*>(root)&&root->parentWidget())root=root->parentWidget()->window();
        QJsonObject scope{{"class",root->metaObject()->className()},{"kind","control"}},q{{"class",e.w->metaObject()->className()},{"kind",e.kind},{"under",scope}};
        if(e.w==root&&e.kind=="control")q=scope;
        else if(!e.w->objectName().isEmpty())q["name"]=e.w->objectName();
        else if(qobject_cast<QLineEdit*>(e.w.data())){
            for(auto p=e.w->parentWidget();p&&p!=root;p=p->parentWidget())if(p->inherits("CQTextureControl")||p->inherits("CQSliderControl")){
                auto labels=p->findChildren<QLabel*>();for(auto label:labels)if(!uiLabel(label->text()).isEmpty()&&label->text().size()<80){q["class"]="QLineEdit";q["under"]=QJsonObject{{"class",p->metaObject()->className()},{"label",uiLabel(label->text())},{"under",scope}};break;}break;
            }
        }else if(qobject_cast<QPushButton*>(e.w.data())&&e.w->parentWidget()&&e.w->parentWidget()->inherits("CQFeaturesWindow")){q["under"]=QJsonObject{{"class","CQFeaturesWindow"},{"under",scope}};}
        else if(!e.label().isEmpty())q["text"]=e.kind=="menu"?uiLabel(e.label()):e.label();
        try{auto found=resolve(q,true);if(found.w==e.w&&found.kind==e.kind)return q;}catch(...){}
        return {};
    }
    QJsonObject describe(Element e){if(!e.alive())return {{"ok",false},{"error","Target not found"}};QJsonArray why;
        for(auto p=e.w.data();p;p=p->parentWidget())if(auto stack=qobject_cast<QStackedWidget*>(p->parentWidget()))if(stack->currentWidget()!=p)why.append(QJsonObject{{"type","inactive_tab"},{"container",ref(Element{stack})}});
        if(e.index.isValid())if(auto tree=qobject_cast<QTreeView*>(e.w.data()))for(auto p=e.index.parent();p.isValid();p=p.parent())if(!tree->isExpanded(p))why.append(QJsonObject{{"type","collapsed_parent"},{"text",p.data().toString()}});
        if(!e.w->isVisible())why.append(QJsonObject{{"type",e.w->window()->isVisible()?"hidden_ancestor":"window_hidden"}});else if(!e.visible()&&why.isEmpty())why.append(QJsonObject{{"type","scrolled_out_or_clipped"}});
        if(auto modal=qApp->activeModalWidget())if(modal!=e.w->window())why.append(QJsonObject{{"type","blocked_by_modal"},{"window",ref(Element{modal})}});
        return {{"ok",true},{"ref",ref(e)},{"locator",locator(e)},{"class",e.w->metaObject()->className()},{"name",e.w->objectName()},{"kind",e.kind},{"text",e.label().left(500)},{"tooltip",e.w->toolTip()},{"rect",rectJson(e.rect())},{"visible",e.visible()},{"enabled",e.w->isEnabled()&&(!e.action||e.action->isEnabled())},{"focused",e.w->hasFocus()},{"window",ref(Element{e.w->window()})},{"hidden_because",why},{"property",propertyOf(e.w)},{"value",valueOf(e.w)},{"managed_classes",QJsonArray::fromStringList(e.w->managedClassNames)}};}
    QJsonObject brief(Element e){if(!e.w)return {};return {{"ref",ref(e)},{"class",e.w->metaObject()->className()},{"name",e.w->objectName()},{"kind",e.kind},{"text",e.label().left(200)},{"window",e.w->window()->windowTitle()}};}
    void log(QJsonObject o){o["at"]=QDateTime::currentMSecsSinceEpoch();o["seq"]=++eventSequence;events.append(o);while(events.size()>100)events.removeFirst();}
    bool eventFilter(QObject*obj,QEvent*event)override{
        auto type=event->type();
        // Installed on qApp: return before any per-widget work for the events we do not care about.
        if(type!=QEvent::FocusOut&&type!=QEvent::KeyRelease&&type!=QEvent::MouseButtonRelease&&type!=QEvent::KeyPress)return false;
        auto w=qobject_cast<QWidget*>(obj);if(!w||own(w))return false;
        if(qobject_cast<QLineEdit*>(w)&&(event->type()==QEvent::FocusOut||(event->type()==QEvent::KeyRelease&&(static_cast<QKeyEvent*>(event)->key()==Qt::Key_Return||static_cast<QKeyEvent*>(event)->key()==Qt::Key_Enter)))){ref(Element{w});committedAt[w]=QDateTime::currentMSecsSinceEpoch();}
        bool agent=synthetic.contains(event);
        if(type==QEvent::MouseButtonRelease){
            auto mouse=static_cast<QMouseEvent*>(event);
            if(mouse->button()!=Qt::LeftButton)return false;
            // Only native releases have a meaningful timestamp. Untimestamped Qt events
            // can be distinct clicks on the same pixel, including the guide's test input.
            auto timestamp=qint64(mouse->timestamp());
            auto position=mouse->globalPos();
            if(timestamp>0){
                if(timestamp==lastClickTime&&position==lastClickPos)return false;
                lastClickTime=timestamp;
                lastClickPos=position;
            }
            bool onTarget=false;
            if(state=="running"&&step<steps.size()){
                auto current=steps[step].toObject();
                auto target=targetFor(current);
                onTarget=target.w&&target.visible()&&target.rect().adjusted(-3,-3,3,3).contains(position)&&w->window()==target.w->window();
                if(!agent&&onTarget&&(!current.contains("until")||current["until"].isNull()||current["until"].toString()=="click"||manualConfirmation))pendingClick=QDateTime::currentMSecsSinceEpoch();
            }
            log({{"event","click"},{"agent",agent},{"on_target",onTarget},{"target",brief(Element{w})},{"x",position.x()},{"y",position.y()}});
        }
        if(event->type()==QEvent::KeyPress){auto k=static_cast<QKeyEvent*>(event);QString key=QKeySequence(k->modifiers()|k->key()).toString(QKeySequence::PortableText).toLower();log({{"event","key"},{"keys",key},{"agent",agent}});if(!agent&&state=="running"&&step<steps.size()){auto until=steps[step].toObject()["until"];if(keyCondition(until,key))pendingClick=QDateTime::currentMSecsSinceEpoch();}}
        return false;
    }
    bool keyCondition(QJsonValue u,QString key){auto o=u.toObject();if(str(o,"key").toLower()==key)return true;for(auto a:o["any"].toArray())if(keyCondition(a,key))return true;return false;}
    void send(QWidget*w,QEvent*event){synthetic.insert(event);QApplication::sendEvent(w,event);synthetic.remove(event);}
    void mouse(QWidget*w,QPoint global,QEvent::Type type,Qt::MouseButton button=Qt::NoButton,Qt::MouseButtons buttons={},Qt::KeyboardModifiers mods={}){QMouseEvent e(type,w->mapFromGlobal(global),w->window()->mapFromGlobal(global),global,button,buttons,mods,Qt::MouseEventSynthesizedByApplication);send(w,&e);}
    void key(QWidget*w,int key,Qt::KeyboardModifiers mods={},QString text={}){QKeyEvent down(QEvent::KeyPress,key,mods,text),up(QEvent::KeyRelease,key,mods,text);send(w,&down);send(w,&up);}
    QWidget*focus(){return qApp->activePopupWidget()?qApp->activePopupWidget():qApp->focusWidget()?qApp->focusWidget():(qApp->activeModalWidget()?qApp->activeModalWidget():qApp->activeWindow());}
    QJsonObject act(QJsonObject a){
        auto action=str(a,"action");Element e;if(a.contains("target"))e=resolve(a["target"]);
        QWidget*w=e.w;if(!w&&a.contains("target"))throw std::runtime_error("UI target not found");
        if(e.w&&(!e.w->isEnabled()||(e.action&&!e.action->isEnabled())))throw std::runtime_error("Target is disabled");
        if(!w&&a.contains("window"))w=resolve(a["window"]).w;if(!w)w=focus();if(!w)throw std::runtime_error("No target window");
        if(auto modal=qApp->activeModalWidget())if(modal!=w->window()&&!modal->isAncestorOf(w))throw std::runtime_error("A modal dialog blocks the target; operate the dialog first");
        if(e.w&&!e.visible()&&action!="focus"){if(e.index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w))v->scrollTo(e.index);if(!e.visible())throw std::runtime_error("Target is hidden; ui_describe explains why");}
        auto point=[](QJsonObject o){return QPoint(qRound(o["x"].toDouble()),qRound(o["y"].toDouble()));};
        QPoint p=e.w?e.rect().center():w->window()->mapToGlobal(point(a));if(e.w&&a.contains("x"))p=w->window()->mapToGlobal(point(a));
        auto mods=a["modifiers"].toObject();Qt::KeyboardModifiers flags;if(yes(mods,"ctrl"))flags|=Qt::ControlModifier;if(yes(mods,"shift"))flags|=Qt::ShiftModifier;if(yes(mods,"alt"))flags|=Qt::AltModifier;if(yes(mods,"meta"))flags|=Qt::MetaModifier;
        QPoint end=p;if(action=="drag"){auto to=a["to"];if(to.isObject()&&to.toObject().contains("x"))end=w->window()->mapToGlobal(point(to.toObject()));else{auto dest=resolve(to);if(!dest.w||!dest.visible())throw std::runtime_error("Drag destination not found or hidden");end=dest.rect().center();}}
        if(action=="key")parseKey(str(a,"keys"));
        QWidget*target=w;if(e.index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w))target=v->viewport();if(e.kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w))target=v->viewport();
        if(!e.w&&a.contains("x")&&action!="key"&&action!="type"&&action!="paste"&&action!="focus"){
            if(auto child=w->window()->childAt(w->window()->mapFromGlobal(p)))target=child;
            e.w=target;
        }
        if(yes(a,"show")&&e.w){highlights={locator(e)};highlightCache.clear();markText=str(a,"text",action);markUntil=QDateTime::currentMSecsSinceEpoch()+1000;tick();}
        int actionId=++actionSequence;actionResults[actionId]={{"status","queued"},{"ok",true}};
        auto work=[this,a,action,target=QPointer<QWidget>(target),e,p,end,flags,actionId]{
            if(!target){actionResults[actionId]={{"status","failed"},{"ok",false},{"error","Target was destroyed before delivery"}};return;}
            actionResults[actionId]={{"status","delivering"},{"ok",true}};
            struct Finish{Bridge*b;int id;bool deferred=false;~Finish(){if(!deferred&&b->actionResults[id]["status"].toString()=="delivering")b->actionResults[id]={{"status","completed"},{"ok",true}};}} finish{this,actionId};try{
            if(!qApp->activePopupWidget())QApplication::setActiveWindow(target->window());
            if(e.action&&(action=="click"||action=="double_click")){
                if(auto menu=e.action->menu())menu->popup(e.rect().bottomLeft());else e.action->trigger();
                return;
            }
            if(action=="focus"){target->setFocus();return;}
            if(action=="key"){if(e.w)target->setFocus();auto combo=parseKey(str(a,"keys"));key(e.w?target.data():focus(),combo.first,combo.second);return;}
            if(action=="type"||action=="paste"){
                target->setFocus();if(action=="paste"){qApp->clipboard()->setText(str(a,"text"));key(target,Qt::Key_A,Qt::ControlModifier);key(target,Qt::Key_V,Qt::ControlModifier);}else{auto text=str(a,"text");for(int i=0;i<text.size();i++){if(text[i]=='\n')key(target,Qt::Key_Return);else{int count=text.at(i).isHighSurrogate()&&i+1<text.size()&&text.at(i+1).isLowSurrogate()?2:1;if(count==2){QInputMethodEvent input;input.setCommitString(text.mid(i,count));send(target,&input);}else key(target,0,{},text.mid(i,count));i+=count-1;}}}return;
            }
            if(hovered&&hovered!=target){QEvent leave(QEvent::Leave);send(hovered,&leave);hovered->setAttribute(Qt::WA_UnderMouse,false);}hovered=target;
            QEvent enter(QEvent::Enter);send(target,&enter);target->setAttribute(Qt::WA_UnderMouse,true);mouse(target,p,QEvent::MouseMove,Qt::NoButton,{},flags);
            if(action=="hover")return;
            if(action=="scroll"){QWheelEvent wheel(target->mapFromGlobal(p),p,{},QPoint(0,-120*num(a,"amount",3)),Qt::NoButton,flags,Qt::NoScrollPhase,false,Qt::MouseEventSynthesizedByApplication);send(target,&wheel);return;}
            Qt::MouseButton button=action=="right_click"?Qt::RightButton:action=="middle_click"?Qt::MiddleButton:Qt::LeftButton;
            mouse(target,p,QEvent::MouseButtonPress,button,button,flags);
            if(action=="drag"){
                // Queue release before moving, so nested Qt drag loops can still receive it.
                finish.deferred=true;
                QTimer::singleShot(210,qApp,[this,target,end,button,flags,actionId]{if(target){mouse(target,end,QEvent::MouseButtonRelease,button,{},flags);actionResults[actionId]={{"status","completed"},{"ok",true}};}else actionResults[actionId]={{"status","failed"},{"ok",false},{"error","Drag source was destroyed"}};});
                for(int i=1;i<=12;i++)QTimer::singleShot(i*15,qApp,[this,target,p,end,i,button,flags]{if(target)mouse(target,p+(end-p)*i/12,QEvent::MouseMove,Qt::NoButton,button,flags);});
            }else{mouse(target,p,QEvent::MouseButtonRelease,button,{},flags);if(action=="double_click"){mouse(target,p,QEvent::MouseButtonDblClick,button,button,flags);mouse(target,p,QEvent::MouseButtonRelease,button,{},flags);}if(action=="right_click"){QContextMenuEvent menu(QContextMenuEvent::Mouse,target->mapFromGlobal(p),p,flags);send(target,&menu);}}
        }catch(const std::exception&err){actionResults[actionId]={{"status","failed"},{"ok",false},{"error",err.what()}};log({{"event","action_error"},{"action_id",actionId},{"error",err.what()}});}};
        if(!QStringList{"click","double_click","right_click","middle_click","hover","scroll","drag","type","paste","key","focus"}.contains(action))throw std::runtime_error("Unknown UI action");
        // Defer delivery so modal exec() cannot block this RPC's response.
        QTimer::singleShot(yes(a,"show")?350:0,qApp,work);
        QJsonObject record=a;if(e.w){record["target"]=locator(e);record["target_info"]=describe(e);auto fallback=fallbackFor(e);if(!fallback.isEmpty())record["fallback"]=fallback;}record["at"]=QDateTime::currentMSecsSinceEpoch();records.append(record);
        while(actionResults.size()>200)actionResults.remove(actionSequence-200);
        return {{"ok",true},{"queued",true},{"action_id",actionId},{"cursor_moved",false},{"record",record}};
    }
    bool condition(QJsonValue u){
        auto o=u.toObject();if(o.isEmpty())return false;
        for(auto c:o["any"].toArray())if(condition(c))return true;
        if(o.contains("all")){auto conditions=o["all"].toArray();if(conditions.isEmpty())return false;for(auto c:conditions)if(!condition(c))return false;return true;}
        if(o.contains("not"))return !condition(o["not"]);
        for(auto kind:{"exists","visible"})if(o.contains(kind)){auto e=resolve(o[kind],QString(kind)=="exists");return e.w&&(QString(kind)=="exists"||e.visible());}
        for(auto kind:{"text_contains","text_equals","number_equals","tooltip_contains"})if(o.contains(kind)){auto s=o[kind].toObject();auto e=resolve(s["target"],true);if(!e.w||(yes(s,"committed")&&committedAt.value(e.w,0)<entered))return false;if(QString(kind)=="number_equals"){bool ok=false;double value=e.label().toDouble(&ok);return ok&&qAbs(value-s["value"].toDouble())<0.0001;}auto have=contentText(QString(kind)=="tooltip_contains"?e.w->toolTip():e.label()),want=contentText(str(s,"text"));return QString(kind)=="text_equals"?have==want:have.contains(want);}
        if(o.contains("context")){auto s=o["context"].toObject();QJsonValue value=context;for(auto part:str(s,"path").split('.'))value=value.toObject()[part];if(value.isUndefined()||value.isNull())return false;return s.contains("equals")?value==s["equals"]:value.toString().contains(str(s,"contains"));}
        for(auto k:{"selected","inspecting"})if(o.contains(k)){for(auto v:context[k].toArray())if(v.toString()==o[k].toString())return true;return false;}
        if(o.contains("main_screen"))return context["main_screen"]==o["main_screen"];
        if(o.contains("file_saved")){
            auto now=QDateTime::currentMSecsSinceEpoch();if(now<nextFileCheck)return false;nextFileCheck=now+500;
            auto s=o["file_saved"].toObject();auto root=QDir::fromNativeSeparators(str(context,"project"));if(root.isEmpty())return false;
            auto saved=[&](QFileInfo info){if(info.isSymLink()||!info.isFile()||info.size()>32*1024*1024||info.lastModified().toMSecsSinceEpoch()<entered)return false;QFile f(info.absoluteFilePath());if(!f.open(QIODevice::ReadOnly))return false;if(str(s,"contains").isEmpty())return true;auto bytes=f.readAll();return contentText(QString::fromUtf8(bytes.constData(),bytes.size())).contains(contentText(str(s,"contains")));};
            if(s.contains("path"))return saved(QFileInfo(projectFile(str(s,"path"))));
            QList<QPair<QString,int>> pending{{root,0}};int files=0;
            for(int i=0;i<pending.size()&&files<2000;i++){QDir dir(pending[i].first);for(auto info:dir.entryInfoList(QStringList{"*."+str(s,"ext")},QDir::Files|QDir::NoSymLinks)){++files;if(saved(info))return true;}
                if(pending[i].second>=12)continue;for(auto info:dir.entryInfoList(QDir::Dirs|QDir::NoDotAndDotDot|QDir::NoSymLinks)){auto rel=QDir(root).relativeFilePath(info.absoluteFilePath());if(info.fileName().startsWith('.')||QStringList{"Libraries","addons","obj","bin","node_modules"}.contains(info.fileName())||rel.startsWith("Art/build",Qt::CaseInsensitive)||rel.startsWith("Art/baseline",Qt::CaseInsensitive))continue;pending.append({info.absoluteFilePath(),pending[i].second+1});}}
            return false;
        }
        return false;
    }
    void enterStep(bool previous=false){invalidateTarget();MemoScope scope(this);dialogPolicy();entered=QDateTime::currentMSecsSinceEpoch();pendingClick=0;scrollTries=0;nextScroll=0;nextFileCheck=0;preSatisfied=false;manualConfirmation=previous;presentedWindow=nullptr;activationSucceeded=false;currentElement={};targetError.clear();if(step<steps.size()){try{preSatisfied=condition(steps[step].toObject()["until"]);}catch(...){}manualConfirmation=manualConfirmation||preSatisfied;auto s=steps[step].toObject();if(s.contains("project_path"))qApp->clipboard()->setText(QDir::toNativeSeparators(projectFile(str(s,"project_path"))));else if(s.contains("clipboard"))qApp->clipboard()->setText(str(s,"clipboard"));}log({{"event","step_enter"},{"index",step}});}
    void advance(QString why){if(state!="running")return;log({{"event","advanced"},{"index",step},{"how",why}});step++;if(step>=steps.size()){state="completed";dialogPolicy();clearOverlays();if(bubble)bubble->hide();}else enterStep();}
    void clearOverlays(){for(auto o:overlays)if(o)o->hide();}
    void clear(){clearOverlays();if(bubble)bubble->hide();highlights={};highlightCache.clear();state="stopped";dialogPolicy();}
    void bubbleFor(QWidget*window,QString text,QRect target){
        if(!bubble||bubble->parentWidget()!=window){if(bubble)bubble->deleteLater();auto b=new QFrame(window,Qt::Tool|Qt::FramelessWindowHint);b->setProperty("aiguideOwn",true);b->setAttribute(Qt::WA_ShowWithoutActivating);b->setStyleSheet("QFrame{background:#202936;color:#ffffff;border:2px solid #ffb74d;border-radius:6px} QLabel{border:none} QPushButton{padding:5px;color:white;background:#394557;border:1px solid #657184}");auto l=new QVBoxLayout(b);auto label=new QLabel(b);label->setWordWrap(true);label->setMinimumWidth(290);label->setMaximumWidth(380);l->addWidget(label);message=label;auto row=new QHBoxLayout;for(auto pair:QVector<QPair<QString,QString>>{{"prev","上一步"},{"next","下一步"},{"locate","定位"},{"restart","从头开始"},{"stop","退出"}}){auto btn=new QPushButton(pair.second,b);row->addWidget(btn);connect(btn,&QPushButton::clicked,this,[this,action=pair.first]{control(action,true);});}l->addLayout(row);bubble=b;}
        if(message->text()!=text){message->setText(text);bubble->adjustSize();}QRect available=window->screen()->availableGeometry();QPoint p=target.isEmpty()?window->mapToGlobal(QPoint(20,40)):target.bottomRight()+QPoint(12,12);p.setX(qBound(available.left(),p.x(),qMax(available.left(),available.right()-bubble->width())));p.setY(qBound(available.top(),p.y(),qMax(available.top(),available.bottom()-bubble->height())));if(bubble->pos()!=p)bubble->move(p);if(!bubble->isVisible()){bubble->show();bubble->raise();}
    }
    QJsonObject status(){return {{"state",state},{"title",title},{"index",step},{"step",step+1},{"total",steps.size()},{"steps",steps},{"log",events},{"waiting",state=="running"&&lastGuide["waiting"].toBool()},{"target",state=="running"&&currentElement.w?describe(currentElement):QJsonObject{}},{"target_error",targetError},{"fallback_used",fallbackUsed},{"pre_satisfied",preSatisfied},{"manual_confirmation",manualConfirmation},{"presented_window",presentedWindow?presentedWindow->windowTitle():QString()},{"activation_succeeded",activationSucceeded},{"restart_armed",restartArmed}};}
    QJsonObject control(QString action,bool human=false){
        if(action=="stop"){clear();return status();}if(action=="next")advance(human?"user_next":"agent_next");
        else if(action=="prev"){step=qMax(0,step-1);state="running";enterStep(true);}
        else if(action=="locate"){if(state=="running"&&step<steps.size()){auto s=steps[step].toObject();auto e=targetFor(s,true);present(e.w?e.w->window():cachedScope.data(),true);if(e.w)scrollIntoView(e);}return status();}
        else if(action=="restart"||action=="restart_clean"){if(human&&!restartArmed){restartArmed=true;log({{"event","restart_armed"}});}else {restartArmed=false;log({{"event","restart_requested"}});if(!human){step=0;state="running";enterStep();}}}
        else if(action=="do_step"){if(step>=steps.size())throw std::runtime_error("No current step");auto s=steps[step].toObject();auto a=s["action"].isObject()?s["action"].toObject():QJsonObject{{"action",str(s,"action","click")}};a["target"]=s["target"];act(a);advance("agent_did_step");}
        else if(action!="next")throw std::runtime_error("Unknown guide control");return status();
    }
    void tick(){
        // A nested event loop (modal exec, drag, processEvents inside editor code) can fire the timer again.
        if(ticking)return;ticking=true;struct Reset{bool&flag;~Reset(){flag=false;}} reset{ticking};MemoScope scope(this);
        QVector<QPair<Element,QString>> marks;QWidget*root=nullptr;QString caption;
        if(markUntil>QDateTime::currentMSecsSinceEpoch()){
            // Highlights may last indefinitely (duration_ms:0); refresh their targets twice a second.
            auto now=QDateTime::currentMSecsSinceEpoch();bool stale=now-highlightAt>=500||highlightCache.size()!=highlights.size();for(auto&e:highlightCache)if(!e.alive())stale=true;
            if(stale){highlightCache.clear();for(auto t:highlights){Element e;try{e=resolve(t);}catch(...){}highlightCache.append(e);}highlightAt=now;}
            for(auto&e:highlightCache)if(e.w){marks.append({e,QString::number(marks.size()+1)});root=e.w->window();}
        }
        if(state=="running"&&step<steps.size()){
            auto now=QDateTime::currentMSecsSinceEpoch();if(pendingClick&&now-pendingClick>=120){advance("click");return;}
            auto s=steps[step].toObject();auto e=targetFor(s);currentElement=e;present(e.w?e.w->window():cachedScope.data());
            // Conditions may walk the UI too; four checks a second are plenty for a human-paced tutorial.
            if(!manualConfirmation&&now>=nextCondition){nextCondition=now+250;try{if(condition(s["until"])){advance("condition");return;}}catch(const std::exception&ex){targetError=QString::fromUtf8(ex.what());}}
            auto modal=qApp->activeModalWidget();bool blocked=e.w&&modal&&modal!=e.w->window()&&!modal->isAncestorOf(e.w);
            if(e.w&&e.visible()&&!blocked){marks.append({e,QString::number(step+1)});root=e.w->window();lastGuide["waiting"]=false;}else{lastGuide["waiting"]=true;if(e.w&&!blocked&&yes(lastGuide,"auto_scroll",true)&&scrollTries<3&&now>=nextScroll){++scrollTries;nextScroll=now+300;scrollIntoView(e);}root=modal?modal:qApp->activeWindow();if(blocked){present(modal);marks.append({Element{modal},QString::number(step+1)});}}
            while(root&&own(root))root=root->parentWidget();
            if(root)root=root->window();
            caption=QString("%1  %2/%3\n%4%5").arg(title).arg(step+1).arg(steps.size()).arg(str(s,"text")).arg(blocked?"\n当前对话框阻挡目标，请先完成或取消它；教程会等待。":manualConfirmation?"\n当前条件已满足或已返回此步，点击高亮处或下一步继续。":(!e.w||!e.visible()?"\n等待目标显露… "+targetError:""));
        }else if(!marks.isEmpty())caption=markText;
        QHash<QWidget*,QVector<QPair<QRect,QString>>> grouped;
        for(auto mark:marks)if(mark.first.visible())grouped[mark.first.w->window()].append({mark.first.rect(),mark.second});
        for(auto win:grouped.keys())if(!overlays.value(win)){overlays[win]=new Overlay(win);connect(win,&QObject::destroyed,this,[this,win]{overlays.remove(win);});}
        for(auto it=overlays.begin();it!=overlays.end();++it)if(auto overlay=it.value()){
            auto win=it.key();auto next=grouped.value(win);
            if(next.isEmpty()||!win->isVisible()||win->isMinimized()){if(overlay->isVisible())overlay->hide();continue;}
            bool nextDim=state=="running"&&dim;
            bool changed=overlay->marks!=next||overlay->dim!=nextDim;
            overlay->marks=next;overlay->dim=nextDim;
            QRect geometry(win->mapToGlobal(QPoint(0,0)),win->size());
            if(overlay->geometry()!=geometry)overlay->setGeometry(geometry);
            if(!overlay->isVisible()){overlay->show();overlay->raise();}
            if(changed)overlay->update();
        }
        if(root&&!own(root)&&!caption.isEmpty())bubbleFor(root,caption,marks.isEmpty()?QRect():marks.last().first.rect());else if(bubble)bubble->hide();
    }
    QJsonValue call(QString command,QJsonObject a){
        MemoScope scope(this);
        if(command=="rehearsal_mode"){rehearsing=yes(a,"enabled");dialogPolicy();return true;}
        if(command=="test_dialog_policy")return QCoreApplication::testAttribute(Qt::AA_DontUseNativeDialogs);
        if(command=="test_file_dialog"){auto dialog=new QFileDialog(fixture);dialog->setWindowTitle("AI Guide test save");dialog->setAcceptMode(QFileDialog::AcceptSave);dialog->setFileMode(QFileDialog::AnyFile);fileFixture=dialog;dialog->show();return describe(Element{dialog});}
        if(command=="test_close_file_dialog"){if(fileFixture){qobject_cast<QDialog*>(fileFixture.data())->reject();delete fileFixture.data();}return true;}
        if(command=="test_overlay_state"){
            QJsonArray out;for(auto overlay:overlays)if(overlay&&overlay->isVisible()){
                auto owner=overlay->parentWidget();auto style=GetWindowLongPtrW(reinterpret_cast<HWND>(overlay->internalWinId()),GWL_EXSTYLE);
                out.append(QJsonObject{{"tool_window",overlay->isWindow()},{"input_transparent",bool(style&WS_EX_TRANSPARENT)},{"no_activate",bool(style&WS_EX_NOACTIVATE)},{"layered",bool(style&WS_EX_LAYERED)},{"geometry_matches",overlay->geometry()==QRect(owner->mapToGlobal(QPoint(0,0)),owner->size())},{"shows",overlay->showCount},{"hides",overlay->hideCount},{"paints",overlay->paintCount}});
            }
            return QJsonObject{{"overlays",out},{"focus",qApp->focusWidget()?ref(Element{qApp->focusWidget()}):QString()}};
        }
        if(command=="test_fixture"){
            if(fixture){fixture->show();fixture->raise();return describe(Element{fixture});}
            auto win=new QMainWindow;win->setObjectName("AIGuideFixture");win->setWindowTitle("AI Guide native test");win->resize(640,540);fixture=win;
            auto center=new QWidget(win);auto layout=new QVBoxLayout(center);win->setCentralWidget(center);
            auto count=new QLabel("Count: 0",center);count->setObjectName("guideCount");count->setProperty("count",0);layout->addWidget(count);
            auto button=new QPushButton("计数 +1",center);button->setObjectName("guideIncrement");layout->addWidget(button);connect(button,&QPushButton::clicked,count,[count]{int n=count->property("count").toInt()+1;count->setProperty("count",n);count->setText("Count: "+QString::number(n));});
            auto edit=new QLineEdit(center);edit->setObjectName("guideText");layout->addWidget(edit);
            auto row=new QFrame(center);row->setObjectName("guideFieldRow");auto fields=new QHBoxLayout(row);fields->addWidget(new QLabel("Labelled value",row));auto value=new QLineEdit(row);value->setObjectName("guideLabelledValue");fields->addWidget(value);layout->addWidget(row);
            auto slider=new QSlider(Qt::Horizontal,center);slider->setObjectName("guideSlider");slider->setRange(0,100);slider->setValue(50);layout->addWidget(slider);
            auto tree=new QTreeWidget(center);tree->setObjectName("guideTree");tree->setHeaderLabel("Objects");auto player=new QTreeWidgetItem(tree,QStringList{"Player"});new QTreeWidgetItem(player,QStringList{"Body"});layout->addWidget(tree);
            auto tabs=new QTabWidget(center);tabs->setObjectName("guideTabs");tabs->addTab(new QLabel("First page",tabs),"One");tabs->addTab(new QLabel("Second page",tabs),"Two");layout->addWidget(tabs);
            auto bar=new QMenuBar(center);layout->insertWidget(0,bar);auto menu=bar->addMenu("Test");auto modal=menu->addAction("Open modal");connect(modal,&QAction::triggered,win,[this,win]{QDialog dialog(win);dialog.setObjectName("guideModal");dialog.setWindowTitle("AI Guide modal");QVBoxLayout layout(&dialog);QLineEdit edit(&dialog);edit.setObjectName("modalText");layout.addWidget(&edit);QPushButton close("Close modal",&dialog);close.setObjectName("modalClose");layout.addWidget(&close);QObject::connect(&close,&QPushButton::clicked,&dialog,&QDialog::accept);dialog.exec();});
            win->show();return describe(Element{win});
        }
        if(command=="test_second_fixture"){
            if(secondFixture)return describe(Element{secondFixture});auto win=new QDialog;win->setWindowTitle("AI Guide second window");win->setObjectName("guideSecondWindow");win->resize(460,260);auto layout=new QVBoxLayout(win);auto button=new QPushButton("Second target",win);button->setObjectName("guideSecondButton");layout->addWidget(button);auto scroll=new QScrollArea(win);scroll->setWidgetResizable(true);auto content=new QWidget;auto rows=new QVBoxLayout(content);for(int i=0;i<24;i++)rows->addWidget(new QLabel(QString("Row %1").arg(i),content));auto field=new QLineEdit(content);field->setObjectName("guideScrolledEdit");rows->addWidget(field);scroll->setWidget(content);layout->addWidget(scroll);secondFixture=win;if(!yes(a,"hidden"))win->show();return describe(Element{win});
        }
        if(command=="test_close_second"){if(secondFixture)delete secondFixture.data();return true;}
        if(command=="test_active_window")return qApp->activeWindow()?qApp->activeWindow()->windowTitle():QString();
        if(command=="test_user_click"){
            auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Fixture target not found");QPoint p=e.rect().center();QMouseEvent down(QEvent::MouseButtonPress,e.w->mapFromGlobal(p),p,Qt::LeftButton,Qt::LeftButton,{}),up(QEvent::MouseButtonRelease,e.w->mapFromGlobal(p),p,Qt::LeftButton,{},{});QApplication::sendEvent(e.w,&down);QApplication::sendEvent(e.w,&up);return true;
        }
        if(command=="test_user_key"){
            auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Fixture target not found");auto combo=parseKey(str(a,"keys"));QKeyEvent down(QEvent::KeyPress,combo.first,combo.second),up(QEvent::KeyRelease,combo.first,combo.second);QApplication::sendEvent(e.w,&down);QApplication::sendEvent(e.w,&up);return true;
        }
        if(command=="test_close_modal"){auto dialog=qobject_cast<QDialog*>(qApp->activeModalWidget());if(dialog&&dialog->objectName()=="guideModal"){dialog->reject();return true;}return false;}
        if(command=="test_close"){if(fixture){if(auto modal=qApp->activeModalWidget())if(fixture->isAncestorOf(modal))throw std::runtime_error("Close the fixture modal before destroying its owner");fixture->close();fixture->deleteLater();fixture=nullptr;}return true;}
        if(command=="info")return QJsonObject{{"qt",qVersion()},{"native_ui",true},{"cursor_independent",true},{"modal_safe",true},{"virtual_items",QJsonArray{"tree","tab","menu","graphic"}}};
        if(command=="context"){context=a;return true;}
        if(command=="events"){int since=num(a,"since",0);QJsonArray copy;for(auto v:events)if(v.toObject()["seq"].toInt()>since)copy.append(v);if(yes(a,"drain"))events={};return copy;}
        if(command=="state"){QJsonArray out;for(auto w:roots())if(w->isVisible())out.append(describe(Element{w}));return QJsonObject{{"windows",out},{"modal",qApp->activeModalWidget()?QJsonValue(describe(Element{qApp->activeModalWidget()})):QJsonValue()},{"focus",focus()?describe(Element{focus()}):QJsonObject()},{"guide",status()}};}
        if(command=="snapshot"||command=="find"){
            QWidget*under=nullptr;if(a.contains("root"))under=resolve(a["root"]).w;if(a.contains("under"))under=resolve(a["under"]).w;
            if((a.contains("root")||a.contains("under"))&&!under)return QJsonObject{{"total",0},{"elements",QJsonArray{}},{"scope_error","Parent window or control is not available"}};
            QJsonArray out;int total=0;for(auto e:elements(under,num(a,"max_items",40),command=="find"?str(a,"kind","any"):QString("any"),command=="find"?&a:nullptr)){if(command=="find"?!match(e,a):(yes(a,"visible_only",true)&&!e.visible()))continue;total++;if(out.size()<num(a,command=="find"?"limit":"max_nodes",command=="find"?20:600))out.append(describe(e));}return QJsonObject{{"total",total},{"elements",out}};
        }
        if(command=="describe")return describe(resolve(a["target"]));
        if(command=="act")return act(a);
        if(command=="action_status")return actionResults.value(num(a,"id"),{{"status","unknown"},{"ok",false}});
        if(command=="highlight"){highlights=a["targets"].toArray();highlightCache.clear();if(highlights.isEmpty()&&a.contains("target"))highlights.append(a["target"]);markText=str(a,"text");auto duration=num(a,"duration_ms",6000);markUntil=duration==0?std::numeric_limits<qint64>::max():QDateTime::currentMSecsSinceEpoch()+duration;tick();return QJsonObject{{"ok",true},{"targets",highlights.size()}};}
        if(command=="guide"){invalidateTarget();steps=a["steps"].toArray();if(steps.isEmpty())throw std::runtime_error("Guide requires steps");lastGuide=a;lastGuide["waiting"]=false;restartArmed=false;title=str(a,"title","AI Guide");step=0;dim=yes(a,"dim",true);state="running";dialogPolicy();highlights={};highlightCache.clear();enterStep();tick();return status();}
        if(command=="guide_status")return status();
        if(command=="guide_control")return control(str(a,"action"));
        if(command=="clear"){clear();return true;}
        if(command=="close_window"){auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Window not found");e.w->window()->close();return true;}
        if(command=="screenshot"){
            auto e=a.contains("target")?resolve(a["target"]):a.contains("window")?resolve(a["window"]):Element{qApp->activeModalWidget()?qApp->activeModalWidget():qApp->activeWindow()};
            if(!e.w)throw std::runtime_error("No window to capture");QWidget*w=e.w->window();auto pix=w->screen()->grabWindow(w->winId());if(pix.isNull())pix=w->grab();
            // Owned transparent tool windows are not children in a window-DC capture.
            // Composite our layer explicitly so screenshots show the actual instruction.
            {QPainter painter(&pix);auto overlay=overlays.value(w);if(overlay&&overlay->isVisible())overlay->draw(painter);if(bubble&&bubble->isVisible()&&bubble->parentWidget()==w)bubble->render(&painter,bubble->mapToGlobal({0,0})-w->mapToGlobal({0,0}));}
            auto scale=pix.devicePixelRatio();if(a.contains("target")&&yes(a,"crop",true)){auto r=e.rect();r.translate(-w->mapToGlobal({0,0}));int margin=num(a,"margin",num(a,"padding",80));r=r.adjusted(-margin,-margin,margin,margin).intersected(w->rect());pix=pix.copy(QRect(r.x()*scale,r.y()*scale,r.width()*scale,r.height()*scale));}
            int maxWidth=num(a,"max_width",1600);if(maxWidth>0&&pix.width()>maxWidth)pix=pix.scaledToWidth(maxWidth,Qt::SmoothTransformation);
            auto path=str(a,"path");if(path.isEmpty()||!pix.save(path,"PNG"))throw std::runtime_error("Screenshot save failed");return QJsonObject{{"path",path},{"width",pix.width()},{"height",pix.height()},{"device_pixel_ratio",scale}};
        }
        throw std::runtime_error(("Unknown Qt command: "+command).toStdString());
    }
};
Bridge*bridge=nullptr;
QByteArray dispatch(QByteArray request){try{auto doc=QJsonDocument::fromJson(request);if(!doc.isObject())throw std::runtime_error("Expected JSON object");if(!bridge)bridge=new Bridge;auto o=doc.object();auto result=validUnicode(bridge->call(str(o,"command"),o["args"].toObject()));return QJsonDocument(QJsonObject{{"ok",true},{"result",result}}).toJson(QJsonDocument::Compact);}catch(const std::exception&e){return QJsonDocument(QJsonObject{{"ok",false},{"error",e.what()}}).toJson(QJsonDocument::Compact);}}
}
extern "C" __declspec(dllexport) char* guide_qt_json(const char*json){
    QByteArray result;if(!qApp)return _strdup("{\"ok\":false,\"error\":\"Qt application is not running\"}");
    auto request=QByteArray(json);try{onGui([&]{result=dispatch(request);});}catch(const std::exception&e){result=QJsonDocument(QJsonObject{{"ok",false},{"error",e.what()}}).toJson(QJsonDocument::Compact);}
    return _strdup(result.constData());
}
extern "C" __declspec(dllexport) void guide_qt_free(void*p){free(p);}
extern "C" __declspec(dllexport) void guide_qt_invoke(void(__cdecl*callback)(void*),void*state){if(!qApp)return;try{onGui([=]{callback(state);});}catch(...){}}
