var miner;
function gridInit(scope, filter) {
    var sortingOrder = 'Members';
    scope.sortingOrder = sortingOrder;
    scope.reverse = true;
    scope.filteredItems = [];
    scope.groupedItems = [];
    scope.itemsPerPage = SiteVars.GenericPageSize;
    scope.pagedItems = [];
    scope.currentPage = 0;

    function clearChecks() {
        try {
            for (var i = 0; i < scope.items.length; i++)
                scope.items[i].IsChecked = false;

            $('#chkAll').prop('checked', false);
        } catch (e) {
            //no point
        }
    };

    scope.range = function (start, end) {
        var ret = [];
        if (!end) {
            end = start;
            start = 0;
        }
        for (var i = start; i < end; i++) {
            ret.push(i);
        }
        return ret;
    };

    scope.hasChecked = function () {
        var i = scope.items.length;
        var checked = [];
        while (i--) {
            var item = scope.items[i];
            if (item.IsChecked === true) {
                return true;
            }
        }
        return false;
    }

    scope.prevPage = function () {
        if (scope.currentPage > 0) {
            scope.currentPage--;
        }
        clearChecks();
    };

    scope.nextPage = function () {
        if (scope.currentPage < scope.pagedItems.length - 1) {
            scope.currentPage++;
        }
        clearChecks();
    };

    var searchMatch = function (haystack, needle) {
        if (!needle) {
            return true;
        }
        if (!haystack) {
            return false;
        }
        return haystack.toString().toLowerCase().indexOf(needle.toString().toLowerCase()) !== -1;
    };

    scope.search = function () {
        if (scope.query != undefined) {
            scope.query = scope.query.trim();
        }
        if (scope.items.length > 0 && scope.items[0].Members == undefined && scope.sortingOrder == 'Members') {
            scope.sortingOrder = 'M';
        }
        scope.filteredItems = filter('filter')(scope.items, function (item) {
            if (scope.sortingOrder !== '' && item[scope.sortingOrder] === null) return false;
            if (scope.sliderConfig !== undefined && !scope.sliderConfig.disabled) {
                var flt = item.Age !== undefined ? item.Age : (item.M !== undefined ? item.M : item.Members);
                //range filter
                if (scope.sliderConfig.userMax() < flt) {
                    return false;
                }
                //range filter
                if (scope.sliderConfig.userMin() > flt) {
                    return false;
                }
            }
            if (scope.dropdownmodel !== undefined && scope.dropdownmodel.length > 0) {
                var ret = false;
                if (item.T !== undefined && item.T != '') {
                    for (var h = 0; h < scope.dropdownmodel.length; h++) {
                        if (item.T.indexOf(scope.dropdownmodel[h]) >= 0) {
                            ret = true;
                            break;
                        }
                    }
                }
                if (!ret) {//if no selected category
                    return ret;
                }
            }
            if (scope.dropdownmodelcountry !== undefined && scope.dropdownmodelcountry.length > 0) {
                var ret = false;
                if (item.c !== undefined) {
                    for (var h = 0; h < scope.dropdownmodelcountry.length; h++) {
                        if (item.c == scope.dropdownmodelcountry[h].id) {
                            ret = true;
                            break;
                        }
                    }
                }
                if (!ret) {//if no selected category
                    return ret;
                }
            }
            if (scope.city !== undefined && scope.city != 'all' && scope.city != item.City) {
                return false;
            }
            if (scope.vip !== undefined && scope.vip != 'all' && scope.vip != item.V) {
                return false;
            }
            for (var attr in item) {
                if (attr == 'Image') continue;
                if (searchMatch(item[attr], scope.query)) {
                    if (scope.datatype != 1 && scope.datafilter != 'all') {
                        if (scope.filterfield == 'Sex') {
                            //special filter for VkUsers
                            return item[scope.filterfield].indexOf(scope.datafilter) > -1;
                        } else {
                            return item[scope.filterfield] === scope.datafilter;
                        }
                    }
                    return true;
                }
            }
            return false;
        });
        if (scope.sortingOrder !== '') {            
            scope.filteredItems = filter('orderBy')(scope.filteredItems, scope.sortingOrder, scope.reverse);
        }
        scope.currentPage = 0;
        scope.groupToPages();
        //scope.requestFromServer = false;//если будет мало результатов, то догрузить
        scope.rowSpinOff(scope.filteredItems);
    };

    scope.groupToPages = function () {
        scope.pagedItems = [];

        for (var i = 0; i < scope.filteredItems.length; i++) {
            if (i % scope.itemsPerPage === 0) {
                scope.pagedItems[Math.floor(i / scope.itemsPerPage)] = [scope.filteredItems[i]];
            } else {
                scope.pagedItems[Math.floor(i / scope.itemsPerPage)].push(scope.filteredItems[i]);
            }
        }
    };

    scope.setPage = function () {
        scope.currentPage = this.n;
        clearChecks();
    };

    scope.sort_by = function (newSortingOrder, e) {
        if (scope.sortingOrder == newSortingOrder)
            scope.reverse = !scope.reverse;

        scope.sortingOrder = newSortingOrder;

        $('th i').each(function () {
            $(this).removeClass('fa-sort-asc').removeClass('fa-sort-desc').addClass('fa-sort');
        });

        if (scope.reverse) {
            $(e.target).removeClass('fa-sort').addClass('fa-sort-asc');
        } else {
            $(e.target).removeClass('fa-sort').addClass('fa-sort-desc');
        }
        scope.search();
    };

    scope.gridFilter = function (field, value, fltr, fltrname) {
        if (fltrname == undefined) {
            fltrname = 'fltr';
        }
        $("#" + fltrname +"-1").removeClass('active');
        $("#" + fltrname +"-2").removeClass('active');
        $("#" + fltrname +"-3").removeClass('active');
        $("#" + fltrname +"-4").removeClass('active');
        $("#" + fltrname +"-5").removeClass('active');
        $("#" + fltrname +"-6").removeClass('active');
        $("#" + fltrname +"-7").removeClass('active');
        $("#" + fltrname +"-8").removeClass('active');

        $("#" + fltrname +"-" + fltr).addClass('active');

        scope.datatype = fltr;
        scope.datafilter = value;

        scope.filterfield = field;//??

        scope.search();//filter+order
        return false;
    };

    scope.gridFilterShorts = function () {
        if ($("#fltr-s").hasClass('active')) {
            $("#fltr-s").removeClass('active');
            scope.query = '';
            scope.datatype = 1;
            scope.filterfield = 'G';
            //sort_by('M', $event);
            scope.sortingOrder = 'M';
        } else {
            $("#fltr-s").addClass('active');
            scope.datatype = 0;
            scope.datafilter = 0;
            scope.filterfield = "U3";//number of videos 3 months
            //sort_by('S3', $event);
            scope.sortingOrder = 'S3V';
        }
        scope.search();//filter+order
        return false;
    };

    scope.gridFilterGlasses = function (value, fltr) {
        $("#fltr2-1").removeClass('active');
        $("#fltr2-2").removeClass('active');
        $("#fltr2-3").removeClass('active');
        $("#fltr2-" + fltr).addClass('active');
        scope.glasses = value;
        scope.search();//filter+order
        return false;
    };

    scope.gridFilterCity = function (value, fltr) {
        $("#fltr2-1").removeClass('active');
        $("#fltr2-2").removeClass('active');
        $("#fltr2-3").removeClass('active');
        $("#fltr2-4").removeClass('active');
        $("#fltr2-" + fltr).addClass('active');
        scope.city = value;
        scope.search();//filter+order
        return false;
    };

    scope.gridFilterVip = function (value, fltr) {
        $("#fltr2-1").removeClass('active');
        $("#fltr2-2").removeClass('active');
        $("#fltr2-3").removeClass('active');
        $("#fltr2-4").removeClass('active');
        $("#fltr2-" + fltr).addClass('active');
        scope.vip = value;
        scope.search();//filter+order
        return false;
    };

    scope.checkAll = function (e) {
        for (var i = 0; i < scope.pagedItems[scope.currentPage].length; i++) {
                // for others toggle all
                scope.pagedItems[scope.currentPage][i].IsChecked = e.target.checked;
        }
    };

    scope.itemsChecked = function () {
        var i = scope.filteredItems.length;
        while (i--) {
            var item = scope.filteredItems[i];
            if (item.IsChecked === true) {
                return true;
            }
        }
        return false;
    }

    scope.rowSpinOff = function (items) {
        if (items.length >= 20) {  //целиковая страница готова
            $('#tr-spinner').hide();
            scope.requestFromServer = false;//чтобы след раз был запрос
        }
        //экономим ресурсы!
        else if (!scope.requestFromServer) {//нужны еще данные с сервера
            $('#tr-spinner').show();
            $('#div-spinner').html('<i class="fa fa-2x fa-spinner fa-spin"></i>&nbsp;&nbsp;' + BlogAdmin.i18n.loadingText);
            scope.requestFromServer = true;
            scope.finishedFromServer = false;
            scope.load();
        }
        else if (items.length == 0 && scope.finishedFromServer) {//items == 0 и загрузка с сервера завершена
            scope.requestFromServer = false;//чтобы след раз был запрос
            $('#tr-spinner').show();
            $('#div-spinner').html(BlogAdmin.i18n.empty);
        }
    }

    scope.rowClick = function (linkto, event) {
        if (event.target.nodeName == "TD") {
            open(linkto, "_blank");
        }
    }

    scope.previewShow = function (itemid, title) {
        if (scope.toastId != itemid) {
            scope.toastId = itemid;
            toastr.clear();
            toastr.info('<a target=\'_blank\' href=\'https://' + itemid
                + '\'>' + title + '\n<img src=\'https://graph.' + itemid + '/picture?type=large\'/></a>');
        }
    }

    scope.previewShow2 = function (itemid, title) {
        if (scope.toastId != itemid) {
            scope.toastId = itemid;
            toastr.clear();
            toastr.info('<a target=\'_blank\' href=\'https://' + itemid
                + '\'>' + title + '\n<img src=\'https://a.t30p.ru/?' + itemid + '\' width="200" height="200"/></a>');
        }
    }

    scope.previewShow3 = function (itemid, preview, title) {
        if (scope.toastId != itemid) {
            scope.toastId = itemid;
            toastr.clear();
            toastr.info('<a target=\'_blank\' href=\'https://periscope.tv/' + itemid
                + '\'>' + title + '\n<br/><img src=\'https://storage.yandexcloud.net/periscope/' +
                itemid + '/' +
                preview + '.jpg\' /></a>');
        }
    }

    scope.search();
}